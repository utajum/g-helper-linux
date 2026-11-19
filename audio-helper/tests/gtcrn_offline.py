#!/usr/bin/env python3
"""Offline checks for the denoise model choice.

Runs the real DSP (audio-helper/render, built from main.c) and checks:

  - GTCRN is the default and removes steady noise
  - "NSE rnnoise" switches to RNNoise, "NSE gtcrn" back to GTCRN

GTCRN removes synthetic voices as noise, so speech is not checked here.

Usage: gtcrn_offline.py <path-to-render>
"""
import subprocess
import sys

import numpy as np

FS = 48000
RENDER = sys.argv[1]
FAILS = []


def render(x, *cmds):
    r = subprocess.run([RENDER, *cmds], input=x.astype(np.float32).tobytes(), capture_output=True, check=True)
    return np.frombuffer(r.stdout, np.float32)


def db(x):
    return 10 * np.log10(np.mean(np.square(x, dtype=np.float64)) + 1e-20)


def check(name, ok, detail):
    print(("ok   " if ok else "FAIL ") + name + ": " + detail)
    if not ok:
        FAILS.append(name)


rng = np.random.default_rng(1)
spec = np.fft.rfft(rng.standard_normal(FS * 4))
pink = np.fft.irfft(spec / np.sqrt(np.maximum(np.fft.rfftfreq(FS * 4, 1 / FS), 20)), FS * 4)
noise = pink / np.sqrt(np.mean(pink**2)) * 10 ** (-35 / 20)

y = render(noise)
check("GTCRN removes steady noise", db(y[-2 * FS:]) < -55, "%.1f dBFS from -35 dBFS" % db(y[-2 * FS:]))
r = render(noise, "NSE rnnoise")
check("NSE rnnoise changes the model", abs(db(r[-2 * FS:]) - db(y[-2 * FS:])) > 3,
      "RNNoise %.1f dBFS, GTCRN %.1f dBFS" % (db(r[-2 * FS:]), db(y[-2 * FS:])))

g = render(noise, "NSE rnnoise", "NSE gtcrn")
check("NSE gtcrn switches back", np.array_equal(g, y), "%.1f dBFS" % db(g[-2 * FS:]))

sys.exit(1 if FAILS else 0)
