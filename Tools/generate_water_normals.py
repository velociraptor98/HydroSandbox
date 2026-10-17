"""Generate a seamless water-ripple normal map (OpenGL convention, +Y up).

The height field is a random-phase sum over integer wave vectors on a periodic domain, built with
an inverse FFT, so the result tiles exactly. Slopes come from spectral derivatives rather than
finite differences, which keeps fine ripples sharp without seam artifacts.

    python3 Tools/generate_water_normals.py Assets/HydroSandbox/Textures/Water/WaterRipples_Normal.png
"""

import sys

import numpy as np
from PIL import Image

SIZE = 1024
SEED = 7
# Power-law falloff of the isotropic spectrum: steeper gives smoother, longer swells.
SPECTRUM_EXPONENT = 6.0
# Wavenumber band, in waves per tile.
K_MIN, K_MAX = 2.0, 64.0
STRENGTH = 0.6


def main(out_path: str) -> None:
    rng = np.random.default_rng(SEED)
    k = np.fft.fftfreq(SIZE, d=1.0 / SIZE)
    kx, ky = np.meshgrid(k, k, indexing="xy")
    k_len = np.hypot(kx, ky)

    amplitude = np.zeros_like(k_len)
    band = (k_len >= K_MIN) & (k_len <= K_MAX)
    amplitude[band] = k_len[band] ** (-SPECTRUM_EXPONENT / 2.0)
    # Soft roll-off at the high end avoids a ringing cutoff.
    amplitude *= np.exp(-((k_len / K_MAX) ** 4))

    spectrum = amplitude * (rng.standard_normal(k_len.shape) + 1j * rng.standard_normal(k_len.shape))

    two_pi_over_size = 2.0 * np.pi / SIZE
    slope_x = np.real(np.fft.ifft2(1j * kx * two_pi_over_size * spectrum))
    slope_y = np.real(np.fft.ifft2(1j * ky * two_pi_over_size * spectrum))
    scale = STRENGTH / np.sqrt(np.mean(slope_x**2 + slope_y**2))
    slope_x *= scale
    slope_y *= scale

    normal = np.stack([-slope_x, -slope_y, np.ones_like(slope_x)], axis=-1)
    normal /= np.linalg.norm(normal, axis=-1, keepdims=True)
    pixels = np.clip((normal * 0.5 + 0.5) * 255.0 + 0.5, 0, 255).astype(np.uint8)
    # Image rows run top to bottom; flip so +Y in the map points up in UV space.
    Image.fromarray(pixels[::-1], "RGB").save(out_path)


if __name__ == "__main__":
    main(sys.argv[1])
