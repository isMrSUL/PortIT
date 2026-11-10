"""DDS files with sRGB DXGI formats, opened by Pillow (PortIT 1.0.1, 2026-11-10).

Pillow reads BC1 / BC2 / BC3 / BC7 / RGBA DDS files but not their sRGB-tagged twins: a folder of 2K wrestler mods with
DXGI 78 (BC3_UNORM_SRGB) textures stopped every port with "NotImplementedError: Unimplemented DXGI format 78".
The pixel data of an sRGB format is the same as its plain twin's (only the colour-space tag differs, and PortIT treats
the colour maps as sRGB anyway), so .dds files are opened with the plain code - the same table as the 2K26 archive
reader (Core/CakeSource.cs). install() patches PIL.Image.open once for the engine's process.
"""
import io
import struct

SRGB_TO_PLAIN = {72: 71, 75: 74, 78: 77, 29: 28, 91: 87, 93: 88, 99: 98}


def plain_dds(data):
    """bytes of a DDS file -> the same bytes with an sRGB DXGI format replaced by its plain twin (or None: unchanged)"""
    if len(data) < 148 or data[:4] != b'DDS ' or data[84:88] != b'DX10':
        return None
    fmt = struct.unpack_from('<I', data, 128)[0]
    plain = SRGB_TO_PLAIN.get(fmt)
    if plain is None:
        return None
    out = bytearray(data)
    struct.pack_into('<I', out, 128, plain)
    return bytes(out)


def install():
    from PIL import Image
    if getattr(Image.open, '_portit_dds', False):
        return
    plain_open = Image.open

    def open_dds_aware(fp, mode='r', formats=None):
        if isinstance(fp, (str, bytes)) or hasattr(fp, '__fspath__'):
            path = fp.decode() if isinstance(fp, bytes) else str(fp)
            if path.lower().endswith('.dds'):
                try:
                    with open(path, 'rb') as f:
                        head = f.read(148)
                    if plain_dds(head + b'\0' * max(0, 148 - len(head))) is not None:
                        with open(path, 'rb') as f:
                            data = plain_dds(f.read())
                        if data is not None:
                            return plain_open(io.BytesIO(data), mode, formats)
                except OSError:
                    pass
        return plain_open(fp, mode, formats)
    open_dds_aware._portit_dds = True
    Image.open = open_dds_aware
