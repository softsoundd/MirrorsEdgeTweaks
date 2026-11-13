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

### RTX Remix runtime and bridge (dxvk-remix-mirrorsedge)
- Author: NVIDIA Corporation and Contributors, with changes for Mirror's Edge by softsoundd
- Source: https://github.com/softsoundd/dxvk-remix-mirrorsedge (branch `mirrors-edge-ngx`),
  a fork of https://github.com/NVIDIAGameWorks/dxvk-remix
- License: MIT (the RTX Remix runtime and bridge) and Zlib (the DXVK code that the runtime is built on)
- Copyright: Copyright (c) 2021-2023 NVIDIA CORPORATION & AFFILIATES (runtime),
  Copyright (c) 2022-2023 NVIDIA CORPORATION & AFFILIATES (bridge),
  Copyright (c) 2017-2021 Philip Rebohle, Copyright (c) 2019-2021 Joshua Ashton
- Usage: The RTX Remix NGX renderer, which adds upscaling and frame generation to the game.
  It installs `Binaries\d3d9.dll`, `Binaries\NvRemixLauncher32.exe` and `Binaries\.trex\`.
  Its own third-party components are listed in the fork's `ThirdPartyLicenses.txt` and `bridge/ThirdPartyLicenses.txt`.

### NVIDIA DLSS, Reflex and Nsight Aftermath
- Author: NVIDIA Corporation
- Source: https://github.com/NVIDIA/DLSS, https://developer.nvidia.com/reflex,
  https://developer.nvidia.com/nsight-aftermath
- License: Proprietary NVIDIA SDK licenses, which are reproduced in the fork's `ThirdPartyLicenses.txt`.
- Usage: Shipped with RTX Remix NGX in `Binaries\.trex`: `nvngx_dlss.dll` (DLSS Super Resolution and DLAA),
  `nvngx_dlssg.dll` (DLSS Frame Generation), `nvngx_dlssnr.dll` and `nvngx.dll_dlssnr.dll` (DLSS-NR),
  `NvLowLatencyVk.dll` (Reflex) and `GFSDK_Aftermath_Lib.x64.dll` (GPU crash diagnostics).
- Attribution: RTX Remix NGX uses NVIDIA DLSS.
  NVIDIA, DLSS and Reflex are trademarks and/or registered trademarks of NVIDIA Corporation in the U.S. and/or other countries.

### Intel XeSS
- Author: Intel Corporation
- Source: https://github.com/intel/xess
- License: Intel Simplified Software License (Version October 2022), reproduced below
- Copyright: Intel(R) Xe Super Sampling (XeSS) SDK: Copyright (C) 2025 Intel Corporation
- Usage: `libxess.dll`, the XeSS upscaler shipped with RTX Remix NGX in `Binaries\.trex`.

### AMD FSR (FidelityFX SDK)
- Author: Advanced Micro Devices, Inc.
- Source: https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK
- License: MIT
- Copyright: Copyright (C) Advanced Micro Devices, Inc. All rights reserved.
- Usage: The AMD FSR Upscaling and Frame Generation runtime shipped with RTX Remix NGX in `Binaries\.trex`:
  `amd_fidelityfx_loader_vk.dll`, `amd_fidelityfx_upscaler_vk.dll` and `amd_fidelityfx_framegeneration_vk.dll`.

### Sentry Native and Crashpad
- Author: Sentry and Contributors, and The Crashpad Authors
- Source: https://github.com/getsentry/sentry-native,
  which builds Sentry's fork of Crashpad (https://github.com/getsentry/crashpad)
- License: MIT (Sentry Native) and Apache-2.0 (Crashpad)
- Usage: `crashpad_handler.exe` and `crashpad_wer.dll` in `Binaries\.trex`, which RTX Remix NGX uses to write crash reports.

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

The following license text applies to DXVK, and to the DXVK code in the RTX Remix runtime under its copyright notices above.

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

## Intel Simplified Software License (full text)

The following license text applies to Intel XeSS.

```
Intel Simplified Software License (Version October 2022)

Intel(R) Xe Super Sampling (XeSS) SDK: Copyright (C) 2025 Intel Corporation

Use and Redistribution. You may use and redistribute the software, which is provided in binary form only, (the "Software"), without modification, provided the following conditions are met:

* 	Redistributions must reproduce the above copyright notice and these terms of use in the Software and in the documentation and/or other materials provided with the distribution.
* 	Neither the name of Intel nor the names of its suppliers may be used to endorse or promote products derived from this Software without specific prior written permission.
* 	No reverse engineering, decompilation, or disassembly of the Software is permitted, nor any modification or alteration of the Software or its operation at any time, including during execution.

No other licenses. Except as provided in the preceding section, Intel grants no licenses or other rights by implication, estoppel or otherwise to, patent, copyright, trademark, trade name, service mark or other intellectual property licenses or rights of Intel.

Third party software.  "Third Party Software" means the files (if any) listed in the "third-party-software.txt" or other similarly-named text file that may be included with the Software. Third Party Software, even if included with the distribution of the Software, may be governed by separate license terms, including without limitation, third party license terms, open source software notices and terms, and/or other Intel software license terms. These separate license terms solely govern Your use of the Third Party Software.

DISCLAIMER. THIS SOFTWARE IS PROVIDED "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, AND NON-INFRINGEMENT ARE DISCLAIMED. THIS SOFTWARE IS NOT INTENDED FOR USE IN SYSTEMS OR APPLICATIONS WHERE FAILURE OF THE SOFTWARE MAY CAUSE PERSONAL INJURY OR DEATH AND YOU AGREE THAT YOU ARE FULLY RESPONSIBLE FOR ANY CLAIMS, COSTS, DAMAGES, EXPENSES, AND ATTORNEYS' FEES ARISING OUT OF ANY SUCH USE, EVEN IF ANY CLAIM ALLEGES THAT INTEL WAS NEGLIGENT REGARDING THE DESIGN OR MANUFACTURE OF THE SOFTWARE.

LIMITATION OF LIABILITY. IN NO EVENT WILL INTEL BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

No support.  Intel may make changes to the Software, at any time without notice, and is not obligated to support, update or provide training for the Software.

Termination. Your right to use the Software is terminated in the event of your breach of this license.

Feedback.  Should you provide Intel with comments, modifications, corrections, enhancements or other input (“Feedback”) related to the Software, Intel will be free to use, disclose, reproduce, license or otherwise distribute or exploit the Feedback in its sole discretion without any obligations or restrictions of any kind, including without limitation, intellectual property rights or licensing obligations.

Compliance with laws.  You agree to comply with all relevant laws and regulations governing your use, transfer, import or export (or prohibition thereof) of the Software.

Governing law.  All disputes will be governed by the laws of the United States of America and the State of Delaware without reference to conflict of law principles and subject to the exclusive jurisdiction of the state or federal courts sitting in the State of Delaware, and each party agrees that it submits to the personal jurisdiction and venue of those courts and waives any objections. THE UNITED NATIONS CONVENTION ON CONTRACTS FOR THE INTERNATIONAL SALE OF GOODS (1980) IS SPECIFICALLY EXCLUDED AND WILL NOT APPLY TO THE SOFTWARE.
```

## License texts

- `GPL-3.0`: https://www.gnu.org/licenses/gpl-3.0.html
- `LGPL-2.1`: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
- `Apache-2.0`: https://www.apache.org/licenses/LICENSE-2.0
