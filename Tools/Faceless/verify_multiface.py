"""Exercise production GPU skin/composite functions with six synthetic faces.

Run via verify_skin.py --multiface-only --work <fixtures> [--egl libEGL.so.1].
This verifies compositor/reconstruction behavior, NOT Unity UI or detection.
"""
import json
import numpy as np
from PIL import Image
from verify_skin import fit_regions


def multi_face_tests(work, surface, tex, render, read, reconstruct):
    canonical = np.array([[float(x) for x in row.split()[1:]]
                          for row in (work / 'canonical_face_model.obj').read_text().splitlines()
                          if row.startswith('v ')])
    canonical -= canonical.mean(0)
    width, height = 768, 512
    yy, xx = np.mgrid[:height, :width]
    pixels = np.stack((xx + .5, yy + .5), axis=-1)
    raw = np.ones((height, width, 4), dtype='f4')
    raw[:, :, :3] = [.075, .09, .12]
    colors = np.array([[.72, .49, .34], [.40, .25, .16], [.80, .64, .55],
                       [.55, .38, .29], [.66, .44, .38], [.32, .205, .15]])
    data = []
    output = tex((width, height))
    for face in range(6):
        center = np.array([128 + (face % 3) * 256, 128 + (face // 3) * 256])
        points = canonical[:, :2] * 9 + center
        values, groups = fit_regions(points)
        values.update(_CameraSize=[width, height, 1 / width, 1 / height],
                      _Amount=1., _Volume=0., _Grain=.35, _ShowMask=0.,
                      _Stages=np.ones(len(groups)), _VideoVisibility=1., _OverlayOnly=0.,
                      _LocalColorStrength=1., _HighlightSuppression=.85)
        guard = tex((width, height))
        surface.render(points, guard)
        inside = read(guard)[:, :, 0] > .5
        shade = ((xx - center[0]) / 200)[:, :, None] * np.array([.055, .04, .025])
        shade += ((yy - center[1]) / 220)[:, :, None] * np.array([.03, .024, .018])
        field = colors[face] + shade
        raw[:, :, :3][inside] = field[inside]
        # Dark, localized synthetic eyes/lips/nose; they must not contaminate
        # another face's donors or the reconstructed center of this face.
        for landmark in [159, 386, 1, 13, 14, 105, 334]:
            dot = ((pixels - points[landmark]) ** 2).sum(-1) < 3.5 ** 2
            raw[:, :, :3][dot & inside] *= .2
        data.append(dict(values=values, guard=guard, inside=inside, points=points))
    source = tex((width, height), raw)
    render('frag', source, output, {'_SurfaceTex': data[0]['guard']},
           dict(data[0]['values'], _Amount=0., _OverlayOnly=0.))
    # GPU texture sampling at a non-power-of-two viewport has tiny rounding
    # differences from CPU array indexing. Compare with the actual camera pass.
    shown_raw = read(output)[:, :, :3]
    for item in data:
        skin = reconstruct(source, item['values'])
        # The real application has independently owned histories. Copy the
        # reusable harness output to test six concurrently retained textures.
        item['skin'] = tex(skin.size, read(skin))

    def draw(item, amount=1., overlay=1.):
        render('frag', source, output, {'_SkinTex': item['skin'], '_SurfaceTex': item['guard']},
               dict(item['values'], _Amount=amount, _OverlayOnly=overlay))
        value = read(output)
        assert np.isfinite(value).all()
        return value

    def over(base, overlay):
        a = overlay[:, :, 3:4]
        return overlay[:, :, :3] * a + base * (1 - a)

    final = shown_raw.copy()
    oracle = final.copy()
    single_errors, color_errors = [], []
    for face, item in enumerate(data):
        baseline = draw(item, overlay=0.)[:, :, :3]
        layer = draw(item)
        item['baseline'] = baseline
        item['layer'] = layer
        composed = over(shown_raw, layer)
        error = float(np.max(np.abs(composed - baseline)))
        assert error < 1e-5, ('single-person final changed', face, error)
        single_errors.append(error)
        final = over(final, layer)
        oracle += baseline - shown_raw
        center = item['points'][1].astype(int)
        output_color = baseline[center[1], center[0]]
        color_error = float(np.linalg.norm(output_color - colors[face]))
        assert color_error < .06, ('wrong face skin color', face, output_color, color_error)
        color_errors.append(color_error)
    six_error = float(np.max(np.abs(final - oracle)))
    assert six_error < 1e-5, ('six faces did not compose independently', six_error)
    union = np.logical_or.reduce([item['layer'][:, :, 3] > 0 for item in data])
    outside_error = float(np.max(np.abs(final[~union] - shown_raw[~union])))
    assert outside_error == 0.

    weights = [0., .15625, .5, .84375, 1., 1.]
    animated = shown_raw.copy()
    expected = animated.copy()
    for item, weight in zip(data, weights):
        animated = over(animated, draw(item, amount=weight))
        expected += (item['baseline'] - shown_raw) * weight
    entry_error = float(np.max(np.abs(animated - expected)))
    assert entry_error < 1e-5, ('entry clocks mixed across faces', entry_error)

    removed = 2
    five = shown_raw.copy()
    for face, item in enumerate(data):
        if face != removed:
            five = over(five, item['layer'])
    others = np.logical_or.reduce([item['inside'] for face, item in enumerate(data) if face != removed])
    other_error = float(np.max(np.abs(five[others] - final[others])))
    lost_error = float(np.max(np.abs(five[data[removed]['inside']] - shown_raw[data[removed]['inside']])))
    assert other_error == 0. and lost_error == 0., ('departure affected others or left ghost', other_error, lost_error)

    # A raw foreground face (zero entry progress) must overwrite a farther
    # effect inside its visible projected face, even at the start of entry.
    near = data[0]
    near_layer = draw(near, amount=0.)
    foreground = over(np.full_like(final, .99), near_layer)
    near_core = near_layer[:, :, 3] == 1
    foreground_error = float(np.max(np.abs(foreground[near_core] - shown_raw[near_core])))
    assert foreground_error < 1e-5

    report = dict(face_count=6, single_final_max_errors=single_errors,
                  six_face_composite_max_error=six_error, per_face_color_error=color_errors,
                  outside_max_error=outside_error, independent_entry_max_error=entry_error,
                  five_remaining_max_error=other_error, removed_layer_ghost_max_error=lost_error,
                  foreground_restore_max_error=foreground_error,
                  unity_editor_tested=False, metal_tested=False, camera_tested=False)
    (work / 'six-face-checks.json').write_text(json.dumps(report, indent=2))
    Image.fromarray((np.clip(final[::-1], 0, 1) * 255).astype('uint8')).save(work / 'six-face-result.png')
    Image.fromarray((np.clip(animated[::-1], 0, 1) * 255).astype('uint8')).save(work / 'six-face-entry.png')
    print('six face checks:', json.dumps(report))
