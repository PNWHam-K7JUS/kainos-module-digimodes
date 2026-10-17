# Third-party parts

## WSJT-X: `decoder/` (jt9.exe, ft8code.exe and their DLLs)

The decoder, and the message packer, are WSJT-X's own programs, distributed unchanged under the GNU General Public License version 3. WSJT-X is Copyright (C) 2001-2026 Joseph H Taylor, Jr, K1JT, and the WSJT Development Group. Its licence and authors are in `decoder/WSJT-X-COPYING.txt` and `decoder/WSJT-X-AUTHORS.txt`.

The source code is at https://sourceforge.net/projects/wsjt/ (and https://wsjt.sourceforge.io/wsjtx.html).

The DLLs alongside them come from the same WSJT-X distribution, and each keeps its own licence:

- `libgfortran`, `libgcc_s`, `libgomp`, `libquadmath`, `libstdc++` and `libwinpthread`: GCC runtime libraries (GPL with the GCC Runtime Library Exception).
- `libfftw3f`: FFTW (GPL).
- `Qt5Core`: Qt (LGPL v3).

## ft8_lib: `Ftx/FtxEncoder.cs`, `Ftx/FtxModulator.cs`

The CRC, the LDPC generator matrix, the tone mapping and the GFSK synthesis are ported from ft8_lib by Karlis Goba, YL3JG (https://github.com/kgoba/ft8_lib), under the MIT License:

> Copyright (c) 2018 Kārlis Goba
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
