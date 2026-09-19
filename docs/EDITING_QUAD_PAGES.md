# Editing quad pages

How to change the contents of a `.quad` page and get the game to load the result. Written
after proving in game, on 2026-09-15, that the Depths takes its ground materials from these
pages; the same round trip is what a material painter would be built on.

Everything below marked **verified** was confirmed by loading the result in the game or by
byte-comparing files. Everything marked **assumed** was not. Do not let the two blur together
later — a previous set of format notes in this project went stale exactly that way.

---

## 1. Where the files are

```
romfs/Cave/cave017/MinusField/Full/C.crbin                    the resource header
romfs/Cave/cave017/MinusField/Full/C.crbin.517a15eb/000277.quad   one page
```

The page directory is the crbin path plus `.` plus the resource id in lowercase hex
(`QuadResource.Id`, also the first four bytes of every page). Page file names come from the
`page_files` array, not from their position: entry `i` is 8 bytes, a `u32` decompressed size
then a `u32` page id, and the file is `{pageId:D6}.quad`. MinusField has 1,202 pages.

A mod only needs to ship the pages it changes. `C.crbin` is untouched by anything in this
document, and the game reads the base game's copy. **Verified.**

## 2. The page container

A romfs page is **four bytes of resource id followed by a plain zstd frame**. There is no
MeshCodec container, no dictionary, and nothing else in the file.

```
eb 15 7a 51   28 b5 2f fd   a0   00 00 09 00   ...
^ resource id ^ zstd magic  ^FHD ^ content size (0x00090000)
```

The frame header descriptor `0xa0` means single-segment, a 4-byte content size field, no
dictionary id, no content checksum. Repacked frames produced by ordinary zstd at level 12
carry the identical descriptor and the game accepts them. **Verified.**

Rules that turned out **not** to apply, each checked before being dismissed:

- The repacked file does not have to be the same size. 730 of 1,202 pages came out larger
  than Nintendo's and all loaded. **Verified.**
- There is no recorded compressed size to keep in sync. `page_files` holds only the
  decompressed size and the page id.
- There is no RESTBL entry to update, and no alignment or padding requirement — original
  sizes are not aligned to anything.
- There is no per-page hash. The four-byte prefix is the resource id, the same for every
  page of the resource.

The decompressed size must not change, since the layout offsets in `C.crbin` are absolute
inside the page. Every edit described here is in place and length-preserving.

`QuadPageSource` also accepts a file whose length equals the decompressed size and treats it
as an already-decompressed page. Whether the **game** accepts that is **untested** — do not
rely on it.

## 3. Finding a quad inside a page

A page is a flat set of fixed regions whose offsets come from one of two layouts in
`C.crbin`, not from the page itself. `layout_types[node]` selects: 0 is the far layout,
anything else the normal layout. For MinusField:

| field                        | far      | normal   |
| ---------------------------- | -------- | -------- |
| `file_size`                  | 0x90000  | 0xec000  |
| `quad_data_offset`           | 0x64000  | 0x64000  |
| `pos_adjust_offset0`         | 0x6c000  | 0x6c000  |
| `pos_adjust_offset1`         | 0x7c000  | 0xd0000  |
| `lod_far_corner_info_offset` | 0x8c000  | 0xe0000  |
| `ci0` / `ci1` / `ci2`        | -1       | 0xe4000 / 0xe8000 / -1 |
| `_30` / `_34`                | 0 / 0x32000 | 0 / 0x32000 |

`file_size` is why pages come in exactly two sizes. Quad data runs from `quad_data_offset`
to `pos_adjust_offset0`, which is 0x8000 bytes, **8 bytes per quad, so 4,096 quads per page
at most**. Per quad:

```
+0   posFlags   position within the node, and the shift
+4   matFlags   the material ids and the single flag
```

Which quads of which page belong to a node comes from the stream tables:

- `stream_dependencies[node]` is 8 bytes, a first and last stream index.
- `stream_info[j]` is 6 bytes: a `u16` where the low 13 bits are the page file index and the
  top 3 are flags, then a `u16` base quad index, then a `u16` quad count.

So walk nodes, take each node's own `quad_data_offset` (far and normal nodes can share a
page, and their layouts differ), walk its streams, and patch `qdo + (base + k) * 8 + 4`.
Collect the work per page — do not assume one offset per file.

**Streams with the flag bits set are skipped** by `QuadMeshBuilder` and by the flood tool:
70,666 of MinusField's 224,100. What they are is **unknown**. In game this shows as islands
of original material among edited quads, so a painter will eventually have to understand
them. **Verified** that the gap is visible.

## 4. The matFlags word

```
bit  0-2    constant 7 in all observed data
bit  3-9    material id, slot 0
bit 10-16   material id, slot 1
bit 17-23   material id, slot 2
bit 24-30   material id, the alternative slot-0 pick
bit 31      "single" flag, consumed by the index map
```

The fragment shader switches its slot-0 pick between bits 17-23 and 24-30 on the two
triangle halves of a quad; the two agree on 99.5% of quads, which is why the builder does
not split them.

**The ids are global array layers.** They index the shared material array
(`MaterialAlb.txtg` / `MaterialCmb.txtg`, 121 layers) directly — no per-resource table, no
remap. Proven by writing layer 117 everywhere, a layer the Depths never uses, and having it
render: the Depths uses only 25 distinct ids and the highest is 108. **Verified.**

Preserve bits 0-2 and bit 31 when writing. **Assumed** to matter; not tested by clearing
them.

## 4a. The `_30` and `_34` blocks are textures

`game::cave::QuadMeshMgr::setupTextures` (1.2.1 main, symbols) takes the two block offsets
out of the page, builds a **320x320** texture from each, copies `320*320*2` bytes and tiles
it with `NVNMgr::toTile`. 320 x 320 x 2 = `0x32000`, exactly one block. They are bound as
`cave_Sampler_QuadMeshMaterialWeights_Ao` and `cave_Sampler_QuadMeshNormals`. **Verified**
by decompilation; the block contents themselves are **verified** to match the layout below
by measurement.

| block | offset | format | contents |
| --- | --- | --- | --- |
| `_30` | 0 | `R5_G5_B5_A1_uNorm` | material weights + AO |
| `_34` | `0x32000` | `R5_G6_B5_uNorm` | normals |

NVN packs `R5_G5_B5_A1` with **R at bits 0-4, G at 5-9, B at 10-14, A at bit 15**. Alpha is
set on all 102,400 texels of every page checked, which is the "constant bit 15" older notes
mention. In-game flood tests put the material blend weight in **green**; red and blue only
changed brightness. `_34` being 565 means `0x8000` is not neutral there - it is B=16, R=0,
G=0.

**Each block is a 64x64 atlas of 5x5 tiles, one tile per quad**, matching a subdivided
quad's 5x5 vertex grid. The address for quad `qi`, vertex `(u, v)`:

```
((qi / 64) * 5 + v) * 320 + (qi % 64) * 5 + u
```

Measured on page 000277: mean absolute difference between neighbouring green values is 1.78
inside a tile against 3.09 and 4.16 across tile boundaries, with no such structure at width
4 or 8. **Verified.**

Note that `Building/QuadMeshBuilder` reads these as 25 *consecutive* halfwords per quad,
which spans five different quads' tiles. That addressing is wrong.

Far-layout pages are also 320x320 but carry 2x2 vertex grids, which does not divide the
atlas the same way. How those pack is **untested**.

## 4b. What the shader confirms

From the decompiled `cave_quadmesh.bfsha` (dump in `cave_quadmesh/`, 30 programs, all
stages). The fragment shader of `cave_quad_mesh_lod0` program 0:

- **The four ids are read exactly as documented above** -
  `bitfieldExtract(floatBitsToUint(in_attr3.y), 3|10|17|24, 7)`, and `in_attr3` is declared
  `flat`, so they are per quad. **Verified.**
- **Each id is hashed**: `id * 0x134B + 0x1612`, and bits 14 and 15 of the result are pulled
  out - two bits per material, almost certainly texture rotation or flip variation so that a
  repeated material does not visibly tile. Consequence for a painter: **the same material id
  looks different in different quads by design**, and that variation is derived, not stored.
- Both page textures are fetched per fragment with a hand-written 2x2 bilinear
  (`texelFetch` plus three `texelFetchOffset`), which is what unfilterable integer formats
  force. Their texel coordinates arrive as varyings from the vertex stage.
- The vertex stage does **programmable vertex pulling** from an SSBO with no input
  attributes, and decodes `gl_VertexID` as: bits 0-4 sub-quad X, bits 5-9 sub-quad Y, bits
  10-31 quad index. That is the same (quad, u, v) triple the atlas addressing in section 4a
  uses, with room for a 32x32 sub-grid of which lod0 uses 5x5.

The atlas stride constants are **not literals in the shader** - they come from
`cave_QM_PageLayoutDataUBO` (`gsys_user2`, 48 bytes, "page table offsets for quad chunk
indexing"), filled by the CPU. To get exact values rather than measured ones, find what
writes that UBO in `game::cave::QuadMeshMgr`.

There is also a `cave_StaticDataUBO` (`gsys_user0`, 3600 bytes) described in the dump's
README as the global material table and splatting weights. Not yet examined; it is the
obvious place to look for the slope term that selects the third slot.

## 5. What is still unknown

- **The blend weights.** The shader blends the three slots per fragment, with weights read
  from runtime maps (`cave_qm_material_weights_ao`, `cave_qm_material_colors`,
  `cave_qm_normals`) that the game generates rather than ships. Since the ids are in the
  pages, the weights must be too, but they are not in `_30`, `_34` or `pos_adjust_offset1`
  as those are currently decoded.
- **The flagged streams**, as above.
- **Whether any other file must agree with a page.** Nothing found so far, but only the
  material ids have been edited.

## 6. Notes for a material painter

What makes a painter tractable:

- Edits are **local and length-preserving**. One quad's ids live in one 32-bit word in one
  page. Nothing references them, nothing has to be recomputed, no table needs updating, and
  no other file needs touching.
- Pages can be repacked freely. Size may change, compression level is free.
- The mapping from quad to world position already exists in
  `Building/QuadMeshBuilder.DecodeNodeGeometry` — node coordinates plus `posFlags` give the
  quad's corner, `GetSidelength` the scale. A painter needs the inverse: world position to
  (page, quad), which is the same arithmetic run backwards over the node tree.
- Ids are per quad, but **the weights are editable too**, at 5x5 per quad, in the `_30`
  atlas described in section 4a. That is better than it looked at first: a painter is not
  limited to flat quads, and can author soft boundaries by writing green values, the same
  resolution the game itself uses. Both edits are length-preserving and page-local.
- A painter that only ever sets all of a quad's id fields to one material needs none of
  this, and leaves the weights - and the AO sharing that texture - untouched. That is the
  safe first version; weights are the upgrade from "repaint a region" to "paint properly".

The `QuadMatFlood` tool in `TotkTerrainViewer/tools/QuadMatFlood` is a working end-to-end
example: read crbin, enumerate streams, decompress, patch, verify the round trip,
recompress, write to a mod tree.

## 7. Gotchas

- **Install one test mod at a time.** Ryujinx enables every mod in the folder. Two mods that
  touch the same pages produce `ファイル読み込み失敗 : ... [ares error no : 12]` and an
  endless load, which looks exactly like a rejected repack and is not one. This cost a
  debugging round.
- Always round-trip a repacked page through decompression and compare with the buffer you
  patched, before writing it. A page the game cannot read is indistinguishable, in game,
  from a page it ignores.
- Layer 117 is Nintendo's red 検証中 placeholder — the loudest possible test material.

## 8. Evidence log

| test | result |
| --- | --- |
| Flood `TerrainArc/MinusField` `.mate` with layer 117 | Depths unchanged |
| Same patch on `TerrainArc/MainField` | surface turned red — mod path works |
| Flood all 11,438,935 quad ids in `cave017/MinusField/Full` with 117 | whole Depths turned red |
| Diff of decompressed pages, original against patched | only 4-byte words at stride 8 inside the quad-data block |
