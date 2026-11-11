# Third-Party Notices

Mirror's Edge Tweaks is licensed under the **GNU General Public License v3.0**
(see [LICENSE](LICENSE)). It incorporates and/or redistributes the third-party
components listed below. Each component remains the property of its respective
authors and is subject to its own license terms.

---

## Bundled / linked libraries

### Eliot.UELib (UELib)
- Author: Eliot van Uytfanghe (EliotVU)
- Source: https://github.com/EliotVU/Unreal-Library
- License: MIT
- Copyright: Copyright (c) Eliot van Uytfanghe
- Usage: Unreal Engine package reading.

### CommunityToolkit.Mvvm
- Author: .NET Foundation and Contributors
- Source: https://github.com/CommunityToolkit/dotnet
- License: MIT
- Copyright: Copyright (c) .NET Foundation and Contributors
- Usage: MVVM framework (observable objects, commands, messaging).

### Microsoft.Extensions.DependencyInjection
- Author: Microsoft / .NET Foundation
- Source: https://github.com/dotnet/runtime
- License: MIT
- Copyright: Copyright (c) .NET Foundation and Contributors
- Usage: Dependency injection container.

### Microsoft.Xaml.Behaviors.Wpf
- Author: Microsoft
- Source: https://github.com/microsoft/XamlBehaviorsWpf
- License: MIT
- Copyright: Copyright (c) 2015 Microsoft
- Usage: XAML behaviors for WPF.

### MaterialDesignThemes (Material Design In XAML Toolkit)
- Author: James Willock and Contributors
- Source: https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit
- License: MIT
- Copyright: Copyright (c) James Willock, Mulholland Software and Contributors
- Usage: UI theming and controls.

---

## Redistributed binaries (downloaded at runtime)

### OpenAL Soft
- Author: Chris Robinson (kcat) and Contributors
- Source: https://github.com/kcat/openal-soft
- License: LGPL-2.1-or-later
- Usage: Replacement audio backend (OpenAL Soft and OpenAL Soft + HRTF options).

### DXVK
- Author: Philip Rebohle (doitsujin) and Contributors
- Source: https://github.com/doitsujin/dxvk
- License: Zlib
- Copyright: Copyright (c) 2017 Philip Rebohle, Copyright (c) 2019 Joshua Ashton,
  Copyright (c) 2019 Robin Kertels, Copyright (c) 2023 Jeffrey Ellison
- Usage: Direct3D 9 to Vulkan translation layer which provides the
  32-bit float depth buffer for the reversed depth setting.

---

## File formats

### LZO1X
- Author: Markus F. X. J. Oberhumer, as part of the LZO library
- Source: http://www.oberhumer.com/opensource/lzo/
- Usage: Unreal Engine package decompression.
- Note: The LZO library is not bundled. Mirror's Edge Tweaks reads the format
  with its own decoder (`MirrorsEdgeTweaks/Services/Lzo1xDecoder.cs`), which
  follows the Linux kernel's description of the format
  (https://docs.kernel.org/staging/lzo.html).

---

## MIT License (full text)

The following license text applies to each of the MIT-licensed components listed
above, under their respective copyright notices (the "above copyright notice"
referenced below).

```
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
```

## zlib License (full text)

The following license text applies to DXVK.

```
                  Copyright (c) 2017 Philip Rebohle
                  Copyright (c) 2019 Joshua Ashton
                  Copyright (c) 2019 Robin Kertels
                  Copyright (c) 2023 Jeffrey Ellison

                          zlib/libpng license

This software is provided 'as-is', without any express or implied
warranty. In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

– The origin of this software must not be misrepresented; you must not
  claim that you wrote the original software. If you use this software
  in a product, an acknowledgment in the product documentation would be
  appreciated but is not required.

– Altered source versions must be plainly marked as such, and must not
  be misrepresented as being the original software.

– This notice may not be removed or altered from any source distribution.
```

## License texts

- `GPL-3.0`: https://www.gnu.org/licenses/gpl-3.0.html
- `LGPL-2.1`: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
