"""Resolve game template tiles with active layers and composed Unity x/z transforms."""
import math

IDENTITY = (1., 0., 0., 1., 0., 0.)


def point(text):
    x, _, z = map(float, text.split())
    return x, z


def transform(parent, placement):
    """Unity Y rotation on x/z; compose scale, then rotation, then translation."""
    a, b, c, d, tx, ty = parent
    px, pz = point(placement.get('pos', '0 0 0'))
    sx, sz = point(placement.get('scale', '1 1 1'))
    angle = math.radians(float(placement.get('roty', '0')))
    co, si = math.cos(angle), math.sin(angle)
    return (a*co*sx-b*si*sx, a*si*sz+b*co*sz,
            c*co*sx-d*si*sx, c*si*sz+d*co*sz,
            a*px+b*pz+tx, c*px+d*pz+ty)


def walk(container, matrix, compounds, active=None, stack=()):
    for child in container:
        if child.tag == 'layer':
            if active is None or child.get('id') in active:
                yield from walk(child, matrix, compounds, active, stack)
        elif child.tag in ('tiles', 'layergroup'):
            yield from walk(child, matrix, compounds, active, stack)
        elif child.tag == 'tile':
            yield child.get('name', ''), transform(matrix, child)
        elif child.tag == 'compoundtile':
            name = child.get('name')
            if name in stack:
                raise ValueError(f'Recursive compound: {name}')
            definition = compounds.get(name)
            if definition is None:
                raise ValueError(f'Missing compound: {name}')
            yield from walk(definition, transform(matrix, child), compounds,
                            set(child.get('activelayers', '').split()), stack + (name,))
