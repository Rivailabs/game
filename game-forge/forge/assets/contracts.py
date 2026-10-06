"""Asset contracts (plan: "Asset contract and production workflow").

One schema per row of the plan's contract table. Every route's output is normalized and validated
against the *same* contract; a route that cannot satisfy it pauses with an incompatibility report.

| Contract   | Required normalized information and acceptance (plan)                                        |
|------------|----------------------------------------------------------------------------------------------|
| Geometry   | Units, axes, pivot, bounds, topology/triangle count, LODs where needed, normals, UVs, named    |
|            | attachments and collider policy                                                               |
| Materials  | Explicit Unity shader mapping, base/normal/roughness/metallic/opacity interpretation, channel  |
|            | packing, texture size, colour space and mobile compression settings                          |
| Skeleton   | Stable joint names/hierarchy, rest pose, orientation, root, skin weights and the approved      |
|            | retarget mapping                                                                              |
| Motion     | Frame rate, duration, clip name, root-motion/in-place declaration, contact timing, loop        |
|            | policy, transition expectations and gameplay event markers                                    |
| Audio      | Source rights, clip purpose, format, sample rate, loudness target, loop boundaries and         |
|            | import/compression policy                                                                     |
| Provenance | Input references and rights, model/service/version, generation date, applicable terms,         |
|            | transformation history, attribution, territory flags and final hashes                         |

Validators return a list of human-readable problems (``[]`` = the contract holds). They never
raise for a failing asset: a failing asset is evidence, not a crash.
"""

from __future__ import annotations

from typing import Literal, Optional

from pydantic import BaseModel, ConfigDict, Field

from .territory import excluded_overlap

# --------------------------------------------------------------------------- geometry

#: Unity convention after Forge normalization: metres, +Y up, +Z forward (FBX exported accordingly).
UNITY_UNITS = "m"
UNITY_UP = "+Y"
UNITY_FORWARD = "+Z"


class Attachment(BaseModel):
    model_config = ConfigDict(extra="forbid")

    name: str  # e.g. bow_grip_socket
    parent: Optional[str] = None  # joint name for skinned assets
    position_m: tuple[float, float, float] = (0.0, 0.0, 0.0)
    rotation_xyzw: tuple[float, float, float, float] = (0.0, 0.0, 0.0, 1.0)


class LOD(BaseModel):
    model_config = ConfigDict(extra="forbid")

    level: int
    triangles: int
    screen_relative_height: float  # Unity LODGroup transition


class GeometryContract(BaseModel):
    model_config = ConfigDict(extra="forbid")

    units: Literal["m"] = UNITY_UNITS
    up_axis: Literal["+Y"] = UNITY_UP
    forward_axis: Literal["+Z"] = UNITY_FORWARD
    pivot: Literal["bottom_center", "center", "grip", "nock", "custom"]
    bounds_size_m: tuple[float, float, float]
    triangles: int
    vertices: int = 0
    topology_hash: str
    lods: list[LOD] = Field(default_factory=list)
    lods_required: bool = False
    has_normals: bool
    uv_layers: int
    uvs_in_unit_square: bool = True
    attachments: list[Attachment] = Field(default_factory=list)
    collider_policy: Literal["none", "box", "capsule", "sphere", "convex_mesh", "mesh"]


def validate_geometry(g: GeometryContract, *, size_m: float | None = None, size_tolerance: float = 0.05,
                      max_triangles: int | None = None, required_attachments: list[str] | None = None,
                      textured: bool = True) -> list[str]:
    p: list[str] = []
    if any(x <= 0 for x in g.bounds_size_m):
        p.append(f"degenerate bounds {g.bounds_size_m}")
    if size_m is not None:
        largest = max(g.bounds_size_m)
        if abs(largest - size_m) > size_m * size_tolerance:
            p.append(f"largest dimension {largest:.3f} m is not {size_m} m (+/-{size_tolerance:.0%})")
    if g.triangles <= 0:
        p.append("no triangles")
    if max_triangles is not None and g.triangles > max_triangles:
        p.append(f"{g.triangles} triangles > budget {max_triangles}")
    if g.lods_required and not g.lods:
        p.append("LODs required for this asset but none provided")
    for prev, nxt in zip(g.lods, g.lods[1:]):
        if nxt.triangles >= prev.triangles:
            p.append(f"LOD{nxt.level} ({nxt.triangles} tris) is not lighter than LOD{prev.level}")
    if not g.has_normals:
        p.append("mesh has no normals")
    if textured and g.uv_layers < 1:
        p.append("textured asset has no UV layer")
    if textured and not g.uvs_in_unit_square:
        p.append("UVs leave the 0-1 square (mobile texture tiling not declared)")
    names = [a.name for a in g.attachments]
    if len(set(names)) != len(names):
        p.append("duplicate attachment names")
    for req in required_attachments or []:
        if req not in names:
            p.append(f"missing named attachment {req}")
    if not g.topology_hash:
        p.append("no topology hash (topology changes could not be detected)")
    return p


# --------------------------------------------------------------------------- materials

#: Explicit URP shader mapping accepted for the mobile profile.
URP_SHADERS = ("Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit",
               "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Baked Lit")
MOBILE_COMPRESSION = ("ASTC_4x4", "ASTC_5x5", "ASTC_6x6", "ASTC_8x8", "ETC2_RGB", "ETC2_RGBA8")
TextureRole = Literal["base_color", "normal", "roughness", "smoothness", "metallic", "opacity", "occlusion",
                      "emission", "mask"]
#: Colour space by texture role: colour data is sRGB, data maps are linear.
SRGB_ROLES = {"base_color", "emission"}


class TextureSpec(BaseModel):
    model_config = ConfigDict(extra="forbid")

    name: str
    file: str
    role: TextureRole
    width: int
    height: int
    colour_space: Literal["sRGB", "linear"]
    compression: str  # mobile format, e.g. ASTC_6x6
    #: For packed maps: channel -> meaning, e.g. {"R": "metallic", "G": "occlusion", "A": "smoothness"}
    channel_packing: dict[str, str] = Field(default_factory=dict)
    sha256: str = ""
    measured_approval: bool = False  # larger than the default ceiling only with measured approval


class MaterialSpec(BaseModel):
    model_config = ConfigDict(extra="forbid")

    name: str
    unity_shader: str
    surface: Literal["opaque", "transparent", "alpha_clip"] = "opaque"
    base_color: Optional[str] = None  # texture name
    normal: Optional[str] = None
    #: How the source roughness becomes URP smoothness ("1 - roughness" or "none" when no map).
    roughness_interpretation: Literal["smoothness = 1 - roughness", "smoothness map", "constant", "none"]
    metallic_interpretation: Literal["metallic map", "packed in mask", "constant", "none"]
    opacity_interpretation: Literal["none", "base alpha", "separate map", "alpha clip"]
    textures: list[TextureSpec] = Field(default_factory=list)


class MaterialsContract(BaseModel):
    model_config = ConfigDict(extra="forbid")

    materials: list[MaterialSpec]


def _pow2(n: int) -> bool:
    return n > 0 and n & (n - 1) == 0


def validate_materials(m: MaterialsContract, *, max_materials: int | None = None, max_texture_px: int = 1024,
                       allow_transparency: bool = False) -> list[str]:
    p: list[str] = []
    if not m.materials:
        p.append("no materials")
    if max_materials is not None and len(m.materials) > max_materials:
        p.append(f"{len(m.materials)} materials > budget {max_materials}")
    for mat in m.materials:
        tex = {t.name: t for t in mat.textures}
        if mat.unity_shader not in URP_SHADERS:
            p.append(f"material {mat.name}: shader {mat.unity_shader!r} is not an explicit URP mapping")
        if mat.surface == "transparent" and not allow_transparency:
            p.append(f"material {mat.name}: transparency needs explicit approval (overdraw on the phone)")
        for ref in (mat.base_color, mat.normal):
            if ref and ref not in tex:
                p.append(f"material {mat.name}: references unknown texture {ref}")
        if mat.normal and mat.normal in tex and tex[mat.normal].role != "normal":
            p.append(f"material {mat.name}: normal slot uses a {tex[mat.normal].role} texture")
        if mat.opacity_interpretation != "none" and mat.surface == "opaque":
            p.append(f"material {mat.name}: opacity declared but surface is opaque")
        for t in mat.textures:
            edge = max(t.width, t.height)
            if edge > max_texture_px and not t.measured_approval:
                p.append(f"texture {t.name} is {edge}px > {max_texture_px}px (needs measured approval)")
            if not (_pow2(t.width) and _pow2(t.height)):
                p.append(f"texture {t.name} is {t.width}x{t.height}, not power-of-two (mobile compression)")
            want = "sRGB" if t.role in SRGB_ROLES else "linear"
            if t.colour_space != want:
                p.append(f"texture {t.name} ({t.role}) must be {want}, is {t.colour_space}")
            if t.compression not in MOBILE_COMPRESSION:
                p.append(f"texture {t.name}: {t.compression} is not a mobile compression setting")
            if t.role == "mask" and not t.channel_packing:
                p.append(f"texture {t.name}: packed mask without a channel-packing declaration")
            for ch in t.channel_packing:
                if ch not in ("R", "G", "B", "A"):
                    p.append(f"texture {t.name}: invalid channel {ch}")
    return p


# --------------------------------------------------------------------------- skeleton


class Joint(BaseModel):
    model_config = ConfigDict(extra="forbid")

    name: str
    parent: Optional[str]
    deforming: bool = True


class SkinWeights(BaseModel):
    model_config = ConfigDict(extra="forbid")

    max_influences: int
    normalized: bool
    unweighted_vertices: int = 0


class SkeletonContract(BaseModel):
    model_config = ConfigDict(extra="forbid")

    skeleton_id: str  # e.g. astra_archer_v1
    joints: list[Joint]
    root: str
    rest_pose: Literal["A", "T", "neutral"]
    orientation: str = "+Y up, +Z forward"
    skin_weights: SkinWeights
    retarget_mapping_sha256: Optional[str] = None  # the owner-approved mapping (exact hash)
    skeleton_hash: str = ""


def validate_skeleton(s: SkeletonContract, *, reference: list[Joint] | None = None, max_deforming: int | None = None,
                      max_weights: int | None = None, approved_mapping: str | None = None,
                      require_mapping: bool = True) -> list[str]:
    p: list[str] = []
    names = [j.name for j in s.joints]
    if len(set(names)) != len(names):
        p.append("duplicate joint names")
    by = {j.name: j for j in s.joints}
    if s.root not in by or by[s.root].parent is not None:
        p.append(f"root {s.root!r} missing or has a parent")
    for j in s.joints:
        if j.parent is not None and j.parent not in by:
            p.append(f"joint {j.name} has unknown parent {j.parent}")
    if reference is not None:
        for r in reference:
            got = by.get(r.name)
            if got is None:
                p.append(f"missing joint {r.name}")
            elif got.parent != r.parent:
                p.append(f"joint {r.name} parent is {got.parent}, expected {r.parent} (hierarchy not stable)")
    deforming = sum(1 for j in s.joints if j.deforming)
    if max_deforming is not None and deforming > max_deforming:
        p.append(f"{deforming} deforming bones > budget {max_deforming}")
    if max_weights is not None and s.skin_weights.max_influences > max_weights:
        p.append(f"{s.skin_weights.max_influences} weights per vertex > budget {max_weights}")
    if not s.skin_weights.normalized:
        p.append("skin weights are not normalized")
    if s.skin_weights.unweighted_vertices:
        p.append(f"{s.skin_weights.unweighted_vertices} vertices have no skin weight")
    if require_mapping:
        if not s.retarget_mapping_sha256:
            p.append("no approved retarget mapping recorded")
        elif approved_mapping is not None and s.retarget_mapping_sha256 != approved_mapping:
            p.append("retarget mapping differs from the owner-approved mapping")
    return p


# --------------------------------------------------------------------------- motion


class EventMarker(BaseModel):
    model_config = ConfigDict(extra="forbid")

    name: str
    time_s: float


class ContactWindow(BaseModel):
    model_config = ConfigDict(extra="forbid")

    name: str  # e.g. left_foot, right_foot, bow_hand_grip
    start_s: float
    end_s: float


class TransitionExpectation(BaseModel):
    model_config = ConfigDict(extra="forbid")

    to_clip: str
    exit_time_s: Optional[float] = None  # None: any time (interrupting transition)
    blend_s: float = 0.15


class MotionContract(BaseModel):
    model_config = ConfigDict(extra="forbid")

    clip_name: str
    frame_rate: float
    duration_s: float
    root_motion: Literal["root_motion", "in_place"]
    loop_policy: Literal["loop", "once", "hold_last"]
    #: Pose distance between first and last frame (radians, max over joints); loops need ~0.
    loop_seam_error: Optional[float] = None
    #: Horizontal root travel over the clip (metres); in-place clips need ~0.
    root_horizontal_travel_m: float = 0.0
    contacts: list[ContactWindow] = Field(default_factory=list)
    transitions: list[TransitionExpectation] = Field(default_factory=list)
    events: list[EventMarker] = Field(default_factory=list)
    skeleton_hash: str = ""
    source_route: str = ""
    cleanup_applied: list[str] = Field(default_factory=list)  # e.g. loop_cleanup, root_motion_conversion


# --------------------------------------------------------------------------- audio


class RightsRecord(BaseModel):
    model_config = ConfigDict(extra="forbid")

    source: str  # library or service name
    licence: str  # licence name / id
    licence_id: str = ""  # receipt or seat id
    url: str = ""
    attribution: str = ""


class AudioContract(BaseModel):
    model_config = ConfigDict(extra="forbid")

    clip_name: str
    purpose: Literal["draw", "release", "clash", "hit", "dodge", "card_select", "land_transfer", "victory", "ui",
                     "music", "ambience"]
    rights: Optional[RightsRecord]
    format: Literal["wav", "ogg", "flac"]
    sample_rate_hz: int
    channels: int
    duration_s: float
    loudness_lufs: float
    loudness_target_lufs: float
    peak_dbfs: float
    loop: bool = False
    loop_start_s: Optional[float] = None
    loop_end_s: Optional[float] = None
    load_type: Literal["DecompressOnLoad", "CompressedInMemory", "Streaming"]
    compression: Literal["PCM", "ADPCM", "Vorbis"]
    sha256: str = ""


def validate_audio(a: AudioContract, *, loudness_tolerance_lu: float = 2.0) -> list[str]:
    p: list[str] = []
    if a.rights is None or not a.rights.licence:
        p.append("no source rights record")
    if a.sample_rate_hz not in (44_100, 48_000):
        p.append(f"sample rate {a.sample_rate_hz} Hz (expected 44.1 or 48 kHz)")
    if a.purpose == "music":
        if a.load_type != "Streaming":
            p.append("music must stream (load type Streaming)")
    else:
        if a.channels != 1:
            p.append("short effects should be mono")
        if a.load_type == "Streaming":
            p.append("short effects should not stream")
    if a.peak_dbfs >= 0.0:
        p.append(f"peak {a.peak_dbfs} dBFS clips")
    if abs(a.loudness_lufs - a.loudness_target_lufs) > loudness_tolerance_lu:
        p.append(f"loudness {a.loudness_lufs} LUFS is outside {a.loudness_target_lufs}+/-{loudness_tolerance_lu} LU")
    if a.loop:
        if a.loop_start_s is None or a.loop_end_s is None:
            p.append("looping clip without loop boundaries")
        elif not (0 <= a.loop_start_s < a.loop_end_s <= a.duration_s):
            p.append("loop boundaries outside the clip")
    return p


# --------------------------------------------------------------------------- provenance


class InputReference(BaseModel):
    model_config = ConfigDict(extra="forbid")

    kind: Literal["prompt", "reference_image", "concept_image", "brief", "base_mesh", "rigged_mesh", "video",
                  "motion_clip", "audio_file", "retarget_mapping", "library_item"]
    description: str
    sha256: str = ""
    uri: str = ""
    rights: str  # e.g. "owner-created", "generated by Forge from owner prompt", "licensed: <library> #<id>"
    rights_holder: str = ""


class TermsRef(BaseModel):
    model_config = ConfigDict(extra="forbid")

    name: str
    url: str = ""
    version: str = ""
    accepted_by: str = ""
    accepted_at: Optional[float] = None


class Transformation(BaseModel):
    model_config = ConfigDict(extra="forbid")

    step: str  # e.g. "generation", "blender_normalize", "decimate", "retarget", "loop_cleanup"
    tool: str
    tool_version: str = ""
    params: dict = Field(default_factory=dict)
    input_sha256: list[str] = Field(default_factory=list)
    output_sha256: list[str] = Field(default_factory=list)
    at: float = 0.0
    manual: bool = False


class TerritoryFlags(BaseModel):
    model_config = ConfigDict(extra="forbid")

    distribution_countries: list[str] = Field(default_factory=list)
    excluded_territories: list[str] = Field(default_factory=list)  # from the route licence (e.g. EU, UK, KR)
    public_demo_allowed: bool = False
    restricted: bool = False  # True when any input/route restricts territory


class ProvenanceRecord(BaseModel):
    model_config = ConfigDict(extra="forbid")

    asset_id: str
    version: int
    stage: str
    inputs: list[InputReference]
    route: str
    model: str = ""
    model_version: str = ""
    service: str = ""  # vendor/service, or "local GPU worker <id>"
    generated_at: float
    terms: list[TermsRef] = Field(default_factory=list)
    transformations: list[Transformation] = Field(default_factory=list)
    attribution: str = ""
    territory: TerritoryFlags = Field(default_factory=TerritoryFlags)
    final_hashes: dict[str, str] = Field(default_factory=dict)  # repository path -> sha256
    provider_job_ids: list[str] = Field(default_factory=list)
    cost_micros: int = 0
    failed_attempts: int = 0


def validate_provenance(r: ProvenanceRecord, *, third_party: bool = True) -> list[str]:
    p: list[str] = []
    if not r.inputs:
        p.append("no input references")
    for i in r.inputs:
        if not i.rights:
            p.append(f"input {i.kind} ({i.description[:40]}) has no rights statement")
    if not r.route:
        p.append("no route recorded")
    if third_party and not r.terms:
        p.append("no applicable terms recorded")
    if not r.transformations:
        p.append("no transformation history")
    if not r.final_hashes:
        p.append("no final hashes")
    for path, h in r.final_hashes.items():
        if len(h) != 64:
            p.append(f"final hash for {path} is not a sha256")
    hits = excluded_overlap(r.territory.distribution_countries, r.territory.excluded_territories)
    if hits:
        p.append(f"asset is excluded in declared distribution territories {hits}")
    return p
