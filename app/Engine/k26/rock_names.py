# (PortIT copy of tools/k26_porter/rock_names.py - the pipeline of the working Rock / Brock ports; only file paths changed, marked PortIT:)
"""2K26 joint names -> names the SvR 2011 Mod Maker's FBX import recognises (Unreal Mannequin), and the
locator joints that are not part of the body."""
SIDES = (('_L', '_l'), ('_R', '_r'))
LOCATORS = {'vector', 'J_Dock', 'J_Opponent', 'J_Cam_Offset', 'J_Cam1', 'J_Cam1_fov', 'J_Cam2', 'J_Cam2_fov',
            'J_FootIK_R', 'J_FootIK_L', 'J_anim_origin'}


def rename(n):
    centre = {'J_Root': 'root', 'J_Hips': 'pelvis', 'J_Spine1': 'spine_01', 'J_Spine2': 'spine_02', 'J_Chest': 'spine_03',
              'J_Neck': 'neck_01', 'J_Head': 'head', 'J_Jaw': 'jaw'}
    if n in centre:
        return centre[n]
    for s, t in SIDES:
        if not n.endswith(s):
            continue
        b = n[:-2]
        limb = {'J_Clavicle': 'clavicle', 'J_Shoulder': 'upperarm', 'J_Elbow': 'lowerarm', 'J_Wrist': 'hand',
                'J_Leg': 'thigh', 'J_Knee': 'calf', 'J_Foot': 'foot', 'J_Toe': 'ball', 'J_Eye': 'eye',
                'H_Elbow%s_tw01' % s: None}
        if b in limb:
            return limb[b] + t
        for f, u in (('Index', 'index'), ('Middle', 'middle'), ('Ring', 'ring'), ('Pinky', 'pinky'), ('Thumb', 'thumb')):
            for k in (1, 2, 3):
                if b == 'J_%sF%d' % (f, k):
                    return '%s_%02d%s' % (u, k, t)
            if b == 'J_%sF0' % f:
                return '%s_metacarpal%s' % (u, t)
    for s, t in SIDES:
        for k in ('01', '02'):
            if n == 'H_Elbow%s_tw%s' % (s, k):
                return 'lowerarm_twist_%s%s' % (k, t)
    return n
