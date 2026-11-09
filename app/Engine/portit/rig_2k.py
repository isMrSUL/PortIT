"""WWE 2K25 / 2K26 skeleton (J_ / H_ names) -> SvR 2011 skeleton (svr11_skeleton.json, 102 bones).

Rules (same spirit as Mod Maker Tools rig_maps.json, which has no 2K25+ rig):
  * J_ joints map 1:1 to SvR motion bones; Spine1+Spine2 -> koshi (weights add), Chest -> mune.
  * 2K forearm twist H_Elbow_*_tw01 / tw02 -> kote_x / kote_x2; neck twist H_Neck_tw -> kubi_x.
  * SvR helpers: belly jiggle H_Gut_Jig_* -> onaka; shoulder pad H_Shld_Pad_* -> *_kata_m.
  * Fingers: F0 = metacarpal (folds into te); F1..F3 -> yubiN0..N2 (thumb has no F0); *_stub folds.
  * Eyes / jaw: J_Eye_* -> *_eye, J_Jaw (+ teeth, tongue) -> d_kuchi, J_upperTeeth -> atama.
  * Every other bone (muscle helpers H_*, RBF collar, hair chains, cameras, IK) folds its weights into its
    nearest mapped ancestor. Bones above the hips (J_Root, vector, cameras) -> root.
  * The face: 2K drives it with blend shapes (FACS), SvR with bones - see face_plan in FINDINGS.md.
"""
import re

CORE = {
    'J_Hips': 'root', 'J_Spine1': 'koshi', 'J_Spine2': 'koshi', 'J_Chest': 'mune',
    'J_Neck': 'kubi', 'H_Neck_tw': 'kubi_x', 'J_Head': 'atama',
    'J_Jaw': 'd_kuchi', 'J_lowerTeeth': 'd_kuchi', 'J_upperTeeth': 'atama',
    'J_Eye_L': 'l_eye', 'J_Eye_R': 'r_eye',
    'H_Gut_Jig_C': 'onaka', 'H_Gut_Jig_L': 'onaka', 'H_Gut_Jig_R': 'onaka',
}
SIDE = {   # 2K name with {S} = L / R  ->  SvR name with {s} = l / r
    'J_Clavicle_{S}': '{s}_sakotsu', 'J_Shoulder_{S}': '{s}_ninoude', 'J_Elbow_{S}': '{s}_kote',
    'J_Wrist_{S}': '{s}_te', 'H_Elbow_{S}_tw01': '{s}_kote_x', 'H_Elbow_{S}_tw02': '{s}_kote_x2',
    'H_Shld_Pad_{S}': '{s}_kata_m',
    'J_Leg_{S}': '{s}_momo', 'J_Knee_{S}': '{s}_sune', 'J_Foot_{S}': '{s}_ashi', 'J_Toe_{S}': '{s}_tsumasaki',
}
FINGER = {'Thumb': 0, 'Index': 1, 'Middle': 2, 'Ring': 3, 'Pinky': 4}
ROOTS = {'vector', 'J_Root', 'root', 'Root', 'IK_Foot_Root', 'IK_Foot_L', 'IK_Foot_R', 'IK_Hand_Root', 'IK_Hand_L', 'IK_Hand_R'}


def direct_map():
    out = dict(CORE)
    for S, s in (('L', 'l'), ('R', 'r')):
        for k, v in SIDE.items():
            out[k.format(S=S)] = v.format(s=s)
        for f, n in FINGER.items():
            for j in (1, 2, 3):
                out['J_%sF%d_%s' % (f, j, S)] = '%s_yubi%d%d' % (s, n, j - 1)
    return out


AEW = {   # AEW Fight Forever (Yuke's, UE4) - same J_ core; its own helpers and a bone-driven face
    'H_Neck_00': 'kubi_x',
    'H_Upperarm_{S}': '{s}_ninoude_x', 'H_Forearm1_{S}': '{s}_kote_x', 'H_Forearm2_{S}': '{s}_kote_x2',
    'H_Leg_{S}': '{s}_momo_x',
    # face: AEW F_* bones -> the SvR face bone of the same area (JUDE animates these)
    'F_Eyebow_{S}_01': '{s}_mayu_00', 'F_Eyebow_{S}_02': '{s}_mayu_01', 'F_Eyebow_{S}_03': '{s}_mayu_02',
    'F_Forehead_{S}_01': '{s}_mayu_03', 'F_Forehead_C_01': 'miken', 'F_Eyebow_C_01': 'miken',
    'F_Cheek_{S}_01': '{s}_hoho', 'F_Cheek_{S}_02': '{s}_hoho', 'F_Cheek_{S}_03': '{s}_hoho01',
    'F_Cheek_{S}_04': '{s}_hoho01', 'F_Cheek_{S}_05': '{s}_hoho02', 'F_Cheek_{S}_06': '{s}_hoho02',
    'F_Cheek_{S}_07': '{s}_meziri', 'F_Cheek_{S}_08': '{s}_meziri',
    'F_Nose_C_01': 'hana', 'F_Nose_C_02': 'hana', 'F_Nose_{S}_01': 'hana', 'F_Nose_{S}_02': 'hana',
    'F_Lip_C_u_01': 'u_kuchi', 'F_Lip_C_u_02': 'u_kuchi_00', 'F_Lip_C_u_03': 'u_kuchi_00',
    'F_Lip_C_b_01': 'd_kuchi_00', 'F_Lip_C_b_02': 'd_kuchi_01', 'F_Lip_C_b_03': 'd_kuchi_01',
    'F_Jaw_C_01': 'd_kuchi', 'F_Jaw_C_02': 'd_kuchi', 'F_Jaw_C_03': 'd_kuchi',
    'F_Jaw_{S}_01': 'd_kuchi', 'F_Jaw_{S}_02': 'd_kuchi', 'F_Jaw_{S}_03': 'd_kuchi', 'F_Jaw_{S}_04': 'd_kuchi',
    'F_Jaw_{S}_05': 'd_kuchi',
}
for _j in range(1, 8):
    AEW['F_Lip_{S}_u_%02d' % _j] = '{s}_u_kuchi' if _j < 5 else '{s}_kuchi'
for _j in range(1, 7):
    AEW['F_Lip_{S}_b_%02d' % _j] = 'd_kuchi_00' if _j < 4 else '{s}_kuchi'
for _j in range(1, 10):
    AEW['F_Eyelid_{S}_u_%02d' % _j] = '{s}_mabuta'
# (lower lids F_Eyelid_*_b_* have no SvR bone: they fold into atama)

FEMALE = {   # 2K weights the breasts to the chest muscle helpers (the jiggle bone carries no weight)
    'H_Pec_{S}_1': '{s}_boin', 'H_Pec_{S}_2': '{s}_boin', 'H_Pec_{S}_3': '{s}_boin', 'H_Pec_Jig_{S}': '{s}_boin',
    'H_Pec_{S}': '{s}_boin', 'H_Pectoralis_{S}': '{s}_boin',   # (AEW)
}


def match(bones, template=None, female=False):
    """bones: [(name, parent name or None)] -> {'map': name -> svr bone for every source bone, 'direct': set,
    'folded': {name: svr}, 'unknown': [names that only reached root through the rig root]}
    female: breasts -> l_boin / r_boin (SvR diva template); with a male template they fold into mune."""
    dm = direct_map()
    names = {b[0] for b in bones}
    if any(n.startswith('F_') or n in ('H_Neck_00', 'H_Upperarm_L') for n in names):   # an AEW skeleton
        for S, s in (('L', 'l'), ('R', 'r')):
            for k, v in AEW.items():
                dm[k.format(S=S)] = v.format(s=s)
    if female:
        for S, s in (('L', 'l'), ('R', 'r')):
            for k, v in FEMALE.items():
                dm[k.format(S=S)] = v.format(s=s)
    tmpl = set(template) if template else None
    parent = dict(bones)
    out, direct, folded, unknown = {}, set(), {}, []
    for n, _ in bones:
        if n in dm and (tmpl is None or dm[n] in tmpl):
            out[n] = dm[n]
            direct.add(n)
    for n, _ in bones:
        if n in out:
            continue
        k = parent.get(n)
        while k is not None and k not in direct:
            k = parent.get(k)
        if k is not None:
            out[n] = folded[n] = out[k]
        else:
            out[n] = 'root'
            if n not in ROOTS and not re.match(r'J_(Dock|Opponent|Cam|FootIK|anim_origin)', n):
                unknown.append(n)
    return {'map': out, 'direct': direct, 'folded': folded, 'unknown': unknown}


def weight_report(mcd, res):
    """share of the skin weight that lands on a directly mapped bone / folds / falls to root unknowingly"""
    import numpy as np
    names = [b['name'] for b in mcd.bones]
    tot = {'direct': 0.0, 'folded': 0.0, 'root_unknown': 0.0}
    for mb in mcd.meshes:
        for i in range(mb['skin_bones'].shape[1]):
            w = mb['skin_weights'][:, i]
            for bi in np.unique(mb['skin_bones'][:, i]):
                s = float(w[mb['skin_bones'][:, i] == bi].sum())
                n = names[bi]
                tot['direct' if n in res['direct'] else 'root_unknown' if n in res['unknown'] else 'folded'] += s
    t = sum(tot.values()) or 1
    return {k: round(100 * v / t, 2) for k, v in tot.items()}


if __name__ == '__main__':
    import glob
    import json
    import os
    import sys
    from mcd_parse import Mcd
    sk = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'svr', 'svr11_skeleton.json')
    template = [b['name'] for b in json.load(open(sk))['bones']]
    missing = sorted(set(direct_map().values()) - set(template))
    print('targets not in the SvR template:', missing or 'none')
    unused = [t for t in template if t not in set(direct_map().values())]
    print('SvR bones no 2K bone drives (%d): %s' % (len(unused), ' '.join(unused)))
    # (rig_2k.py <.mcd files or folders>)
    files = [f for a in sys.argv[1:] for f in (glob.glob(os.path.join(a, '**', '*.mcd'), recursive=True)
                                               if os.path.isdir(a) else [a])]
    for p in files:
        m = Mcd(p)
        bones = [(b['name'], m.bones[b['parent']]['name'] if b['parent'] >= 0 else None) for b in m.bones]
        r = match(bones, template)
        print('%-70s bones %4d direct %3d  weight %s  unknown %d %s' % (
            p.split('2k26')[-1][-70:], len(bones), len(r['direct']), weight_report(m, r), len(r['unknown']),
            r['unknown'][:4]))
