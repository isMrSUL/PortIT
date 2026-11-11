# PortIT engine: Python -> C#

Started 2026-11-11. The Python engine (`app\Engine`, ~12,400 lines) is ported piece by piece into this project
(`PortIT.Engine`). The Python engine stays the reference: every piece is checked against it on the same files with
`EngineNet.Check` (`portit-check <check> <ref.json>`, the references made by `EngineNet.Check\ref\*_ref.py`) and must
give the same result - byte for byte where the output is a file - before PortIT uses it. Releases keep the Python
engine until every test wrestler ports the same through C#.

Rules
- NumPy's float results depend on its BLAS (OpenBLAS picks kernels by CPU): on this PC 1-D dot / norm = fused
  multiply-add chains from 0, the 4x4 vector @ matrix = fused, rows 1, 0, 2, 3, einsum('ij,ij->i') of 3 =
  (p0 + p2) + p1 (measured, Port/Qem.cs). The Python engine itself can differ a little between PCs.
- NumPy sums float64 runs pairwise (8 running sums from 8 values on): use Util/Np.Sum where Python sums a
  float array, or the last bits differ (the 2K23+ weight normalising showed it).
- Port behaviour, not just shape: every fix in the Python code (its comments say why) comes along.
- No new NuGet downloads without asking the user. Offline cache has: SkiaSharp, Pfim (DDS read), SixLabors.ImageSharp
  (+ .Textures), System.IO.Hashing, xunit. Used: Pfim 0.11.3 (BC7 only - its BC1 rounds differently from Pillow),
  SkiaSharp 2.88.9 (PNG / JPEG, as the PortIT window), System.IO.Hashing 9.0.0 (XXH3). Add them to
  THIRD-PARTY-NOTICES.md when the C# engine ships.
- DXT encoding is our own (Imaging/DdsWriter.cs): judged by error against the source, not bytes (Pillow's encoder
  differs between versions anyway: dev 13.0, release 12.3).
- Keep the Python file name in each C# file's summary (`port of svrfmt.py ...`).

Test set (for the later, whole-port checks)
- 2K26 game: Punk (WM27_2011, byte-identical baseline), Warrior WM6 (face paint, knee pads), Abyss (mask + hood),
  AJ Styles 2016 (jacket), Batista, Umaga, Tama Tonga FacePaint, Demon Finn, Rey Mysterio 1999 / 2008 / 2019.
- Mod folders: Downloads\facepaints (2K23 Jeff Hardy, 2K24 Stardust), supportmoregames (2K19-2K25 packs and mods).
- AEW Fight Forever: one wrestler with a mask (Penta).

Reference Python: Dev\AnimIT-deps\python (3.12.10, NumPy 2.5.4, Pillow 12.3.0 - the one the PortIT release
bundles; the Codex runtime used first was removed from the PC on 2026-11-11). Same BLAS orders as measured before.

Run the checks: `dotnet build EngineNet.Check -c Release`, then
`python EngineNet.Check/ref/<x>_ref.py <ref.json> <pacs...>` and `portit-check <x> <ref.json>`.

Pieces, bottom-up (status)
| # | Python | C# | Check | Status |
|---|---|---|---|---|
| 1 | svr/svrfmt.py (BPE, PACH, EPAC), svr/ch_tool.py (EPK8, bundles), w13_char.write_epk8 | Formats/Bpe.cs, Formats/Containers.cs | formats: 396/396 same on ch125, ch104, Rey 1999 classic, Punk full | done |
| 2 | svr/jboy.py (SvR model read / write, POF0, strips) | Formats/Jboy.cs | jboy: 72/72 same on 24 models (read, rewrite byte for byte, triangles) | done |
| 3 | DDS / textures (Pillow open / resize / convert, w13_char.resize_rgba + dds_encode, _dds_levels, dds_compat) | Imaging/Img.cs, Resample.cs, Dds.cs, ImageIO.cs, DdsWriter.cs | images: 182/182 pixel-identical to Pillow (25 files: 2K26 BC1/3/5/7, mod DXT1/5, PNG; Lanczos / bilinear / box, apart, premultiplied, L). dxt: 36/36 - same layout and header, error lower than Pillow's on every texture (normal sheet 3.8 vs 7.4) | done |
| 4 | k26/xxh3.py, full_port.pac_texture_hashes | Port/TexturePackHash.cs (System.IO.Hashing) | hash: 2106/2106 (lengths 0-2100, 4 MB, Punk / Warrior pacs = their real pack file names) | done |
| 5 | readers: mcd_parse.py, jboy_2k.py, mtls.py, cityhash, iostore + ioextract, zenpkg, aew_mesh, aew_tex | Readers/Mcd.cs, YukesPack.cs, Mtls.cs (+ CityHash), Unreal.cs (IoContainer, Oodle, ZenPackage, ZenReader), Aew.cs; Util/Np.cs (NumPy's pairwise sum) | mcd: 88,797/88,797 on 149 models (2K20 / 2K23 / 2K24 / 2K25 / 2K26, bone matrices within 9e-14); pack: 776/776 (18 packs unpacked byte-identical, zlib + Oodle) and 48,296/48,296 on their 36 parts; misc: 561/561 (CityHash, 133 .mtls, 13 AEW containers, 4 AEW meshes + 16 textures - a property both fail on differs only in its error wording, masked) | done |
| 6 | k26/qem.py (simplify) | Port/Qem.cs | qem: 14/14 - Rey Mysterio 1999's head, body, mask, overalls, shoes, fixed and free edges: the same kept points and triangles; C# 0.0 - 0.3 s vs Python 0.5 - 3.9 s | done |
| 7 | k26/rock_build.py, svr11_build.py, model_budget.py, prop_bones.py, w13_char (Conv / bundles / pack_kids) | Build/SvrBuilder.cs (+ Budget), PropBones.cs, Strip.cs | build: 145/145 on Rey 1999 + 2019 classic and Punk full - every model part and data part byte-identical, page counts the same, the pictures handed to the DXT encoder pixel-identical, our encoding error 3.3 - 3.5 (Pillow's higher); C# 1 - 4 s | done |
| 8 | k26/rock_fbx.py, rock_model.py, k26_face.py (the 2K model -> model.json) | Port/RockFbx.cs, Readers/Source2k.cs, Port/Face.cs, Port/FaceTransfer.cs, Port/RockModel.cs (+ Np.Round / Np.PyRound) | fbx: 1,419/1,419 exact (Rey 1999, Punk, Abyss, Tama Tonga FacePaint, 2K24 Stardust). rockmodel: 538/538 on the same five with port_2k.setup's tables (ref/rockmodel_ref.py): every position, normal, uv, triangle, joint value, report / printed line and both sheets' pixels the same; weights the same except on vertices whose source weights hold two exactly equal ones (NumPy's argsort is its AVX2 SIMD sort here, not stable: it may list / keep the other one - 2-32 vertices a wrestler, the check allows a difference only on such a vertex). Python's face-bone order comes from a set of names (changes every run): compared as a set. C# 5-10 s vs Python 19-40 s | done |
| 9 | k26/full_port.py, port_2k.py, brock_model.py, aew_bridge.py, portit/face_paint.py | Port/FullPort.cs, Port/Port2k.cs, Port/AewBridge.cs, Readers/SourceAew.cs, Imaging/Draw.cs | 9a DONE - fix_model chain (card_weights + lever_limits, mouth_cards, gear_weights incl. the AEW GEAR_HEAD / inner-lip branches, hide_head_under, tuck_skin + _closest_on, inner_bridges, split_big; flat_cards written, checked with port_2k): fixmodel 776/776 on the five rockmodel wrestlers' model.json (ref/fixmodel_ref.py: the same input file to both) and a stress run 1,166/1,166 (split at 1,500 triangles, GEAR_HEAD on) - every value of the fixed model.json and every log line the same; C# 0.4-0.9 s vs Python 2-4 s. 9b DONE (but the build loop) - Port/Port2k.cs: setup (probe read, solid_alpha_gear, mouth parts, kinds, the sheet layout with its halving, cutout / shine / solid, hair colours), targets, card_meshes, strand_pieces, brock_model.thin_cards (NumPy's default_rng(7).shuffle: random_interval draws - NpRandom.Seed7 / Shuffle, 10/10 permutations the same), full_port's strand_colour / srgb hair colour, portit/face_paint.prepare, check_green + green_curl_ratio, yukes_pack, full_port.flat_cards: setup 284/284 + 29/29 on ten wrestlers (Rey 1999, Punk, Abyss, Tama Tonga FacePaint, 2K24 Stardust, Demon Finn, Umaga, Warrior WM6, Warrior 2017 WM5, 2K23 Jeff Hardy mod; six with face paint): tables, targets at x1 and x0.73, cards and colours, thinned cards, painted head / normal map pixels, hair map pixels, logs. (Python quirk seen: a wrestler's first port prints its hair texture as None in the probe report, later ports the generated *_color.png - report text only.) 9c DONE - FullPort: pac_limits, heavy_meshes, checks (default_rng(1).choice: NpRandom.Seed1, 6/6 the same; Python sums each drift over a set of names - order changes per run - C# in name order), layout (the 8192 x 4096 sheet), make_pack (DdsWriter.Levels = _dds_levels; OnLevels hook), window_pages, enabled_pack / enable_pack (build_pac = SvrBuilder.Run, checked in piece 7): fullside 43/43 (3 built pacs at normal and forced-low limits, checks on 5 fixed models, layout on Rey 1999 / Punk / Finn / Abyss, packs at 1024 and 4096 with cut cells, 3 bigmodel.log cases, 7 svr2011.toml variants incl. missing / CRLF / no section). Port2k.Build DONE (port_2k.build + _build: face paint, green test, setup, flat cards, the full route's layout / window, the attempts loop with targets / card thinning / strands, rock_model, fix_model, SvrBuilder, the page / index / part limits, checks, the texture pack): port 6/6 end to end vs Python port_2k.build (ref/port_ref.py) - Rey 1999, Punk, Tama Tonga FacePaint, 2K24 Stardust, Demon Finn classic (190) and Punk full (1600): the same pages (167 / 177 / 181 / 183 / 173 / 406), every log / printed / report line, every position, normal, uv, triangle of model.json, checks and notes; weights the same but on 0-4 vertices a port whose source weights tie (NumPy's unstable argsort, see piece 8); the pack's file names carry the hash of our own pac's DXT data (our encoder, piece 7) - checked to match our pac. C# 10-27 s vs Python 25-42 s a port. 9d in progress: portit/source_aew.py DONE - Readers/SourceAew.cs (containers, find, read_file, mesh_package, quat_mat, material_textures incl. the eyes' pupil composite (Pillow's paste blend), hair_level, kind_of, load; ZenPackage.ImportsOff added): aewsrc 675/675 on Penta, Adam Cole, Bryan Danielson (hair Level), Britt Baker - bone worlds exact, every mesh array, every texture's pixels. (Python quirk kept: a hair mesh with a Level gets the name "('<mi>', 'level')" - its cache key.) Imaging/Draw.cs = Pillow 12.3's ImageDraw.polygon fill (its source not quoted by the web reader - the rules measured: points cut to whole pixels, float32 crossings, the corner rule): polygon 18,000/18,000 random triangles pixel-identical (3 sets, the last two never used for fitting; slivers, duplicate points, points off the image). aew_bridge DONE - Port/AewBridge.cs (skin split, head crop, AEW mouth + baked occlusion, eyes, lashes, hair / beard cards, gear, M_InnerMouth + shading, winding, Build in rock_fbx.build's place; RockModel.Source / Port2k.Bridge hooks): AEW ports 4/4 end to end vs Python Bridge + port_2k.build (ref/aewport_ref.py, worker-style): Penta, Bryan Danielson, Adam Cole classic and Penta full - same pages (159 / 183 / 178 / 379), every log / printed / report line (work folder masked), every model.json value; weights differ on 2 tie vertices (Penta's mask: H_Trap's equal mune / kubi halves). Fix on the way: info['meshes'] is a dict (AEW has two M_EYE) - Port2k keeps first place / last value. All piece 8 / 9 checks re-run after: port 578/578, AEW 409/409, setup 284 + Finn 29/29, fixmodel 776/776, rockmodel 538/538, fullside 43/43. (Python set orders kept as sorted ints: aew_bridge's piece ties, head-weight sums - no difference seen.) | done |
| 10 | portit: source_2k, source_aew, face_paint, preview, attires (convert, simplify, rig_2k, face_transfer, weights_fallback: the classic route - the app never asks for it, not ported) | Readers/Source2kModel.cs, Readers/SourceAew.cs (+ roster), Port/Preview.cs, Port/Attires.cs | through the worker check below: list / inspect / source preview / stars pixel- and value-identical; preview.render pixel-identical on the same pacs (render 3/3; its caption is drawn with SkiaSharp's font - Pillow's built-in one not copied, the strip left out of the check) | done |
| 11 | worker.py commands -> an in-process C# API; Core/Engine.cs switches over | EngineNet/Worker.cs (list, inspect, source_preview, port, preview, export_svrmod, install, stars, install_attire, bigmodel - the same JSON in / out); Core/Engine.cs runs it in PortIT.exe (job folders kept; cancel through the progress lines; PORTIT_PYTHON_ENGINE=1 = the old Python engine, developers only); PortIT.csproj references EngineNet and ships no Python (Engine\cake + Engine\bigmodel only); MainWindow: Engine.* -> Core.Engine.* (namespace) | worker: 25 requests through Python's worker.py (a process each, as the app ran it) and C#'s Worker (ref/worker_ref.py, portit-check worker): 3,626 same; the rest explained - pac previews differ as the pacs' DXT data does (piece 7; same pac -> same picture), an error worded by each language, the fake game compared at the end only; the mod folder's manifest / report written CRLF as Python's text mode did. The app run headless (--shot): the 2K26 install listed (557) and Rey 1999 extracted, inspected, drawn in-process. C# 3-6x faster on inspect, ~2.5x on ports | done |

Open bugs to fix in the new engine (reported 2026-11-11, not fixed in Python yet)
- Finn Balor (a mod, "...4)") loses his face paint: find the cause, then check every painted wrestler (game and mod
  folders, 2K23 / 2K24 / 2K26) by comparing the ported head texture with the 2K head + paint.
- Samoa Joe's eyes; grey eyelashes on some models; an option for more vertices (with a crash warning).
- Already fixed in Python (dev only, not released): 190-page ports of mods stuck over the window (open-edged hair /
  beads / laces: rock_model retries with free edges; port_2k thins strand hair).

Piece 8 notes (2026-11-11)
- k26_face uses LAPACK: np.linalg.lstsq (_affine, 2-column fits), np.linalg.solve (the thin-plate spline), and
  rock_model.skin np.linalg.inv (3x3). LAPACK's internal order cannot be copied bit for bit: those values are checked
  to ~1e-9; model.json rounds positions / normals / weights to 5 decimals and uvs to 6, which absorbs that except on
  rounding edges - the check lists every value that differs.
- weights in model.json use Python's round(x, 5) (exact decimal, half-even, then the nearest double) - not C#'s
  Math.Round; positions use np.round (x * 1e5, rint, / 1e5).
- fit_face samples with np.random.default_rng(0).choice(ok, 220, replace=False): needs NumPy's PCG64 + its choice
  algorithm exactly (no other randomness in the pipeline).
- np.percentile (linear), argmin (first), np.unique (sorted) - deterministic; reproduce.
- Found while porting 8a (fixed in BOTH engines, 2026-11-11): a material can point at two textures of one kind
  (19 of 645 cached: painted heads -> head_color + facepaint_color); source_2k picked by Python set order. Now:
  the one named after the material, else the first in the .jmtl (source_2k.pick_by_role / Source2kTextures.PickByRole).
  Related to the Finn face-paint report - check when fixing it.
- Row-wise products measured: (n,3)@(3,) fused 1,0,2; (n,3)@(3,3)[.T] fused 0,1,2 from 0 (turns -0 into +0);
  einsum 3-term (p0+p2)+p1; all 5 - 3000 rows (Util/Rows).
