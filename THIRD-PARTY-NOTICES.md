# Third-party notices

Loudline's own source is licensed under the MIT License in [LICENSE](LICENSE),
copyright (c) 2026 O. Inha. The following components retain their original
copyright notices and licence terms. Distributing the single executable does
not remove the requirement to retain the applicable notices.

## Supplied Lucide icons

The toolbar SVGs supplied for Loudline are Lucide icons, converted to WPF vector
geometry with theme-dependent stroke color.
The original SVG files are retained in `src/Multiloud.App/Assets/Icons`.

https://github.com/lucide-icons/lucide

ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

### Feather-derived icons

Lucide identifies its `power` and `trash` icons as derived from Feather.
Their additional MIT notice is retained here:

The MIT License (MIT)

Copyright (c) 2013-present Cole Bemis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## NAudio 2.2.1

https://github.com/naudio/NAudio

MIT License

Copyright (c) 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

NAudio includes the WDL resampler, under the zlib license:

Copyright (C) 2005 and later Cockos Incorporated

This software is provided 'as-is', without any express or implied warranty.
In no event will the authors be held liable for any damages arising from the
use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not claim
   that you wrote the original software. If you use this software in a product,
   an acknowledgment in the product documentation would be appreciated but is
   not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.

## .NET 8 runtime and Windows Desktop runtime

The portable executable includes Microsoft .NET runtime components, licensed
under the MIT License and other applicable third-party notices.

https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
https://github.com/dotnet/wpf/blob/main/LICENSE.TXT
https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT

The portable archive's `licenses` directory contains the licenses and notices
from the exact resolved runtime packs, together with Windows Desktop notices
distributed by the .NET SDK used to build this executable. Keep these files and
this notice when redistributing Loudline, including when the runtime libraries
are bundled inside the executable.

## Measurement references

ITU-R BS.1770: https://www.itu.int/rec/R-REC-BS.1770
EBU Tech 3342 (Loudness Range): https://tech.ebu.ch/publications/tech3342

Loudline's metering and limiter are original implementations and have not
been certified by these organizations.
