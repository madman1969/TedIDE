# Third-party notices

Tedide is MIT-licensed (see [LICENSE](LICENSE)). It is built on, ships with, or bundles the
third-party work listed below, each under its own licence. The full licence texts that must
accompany a copy are at the end of this file.

## Contents

- [Libraries shipped with Tedide and the Doc Viewer](#libraries-shipped-with-tedide-and-the-doc-viewer)
- [Native code inside those libraries](#native-code-inside-those-libraries)
- [Source code included in Tedide](#source-code-included-in-tedide)
- [Documentation bundled in the Doc Viewer](#documentation-bundled-in-the-doc-viewer)
- [Build-time and development-only tools](#build-time-and-development-only-tools)
- [Programs Tedide runs but does not include](#programs-tedide-runs-but-does-not-include)
- [Licence texts](#licence-texts)

## Libraries shipped with Tedide and the Doc Viewer

NuGet packages whose assemblies are copied next to `Tedide.App.exe` and `Tedide.DocViewer.exe`,
or into the single-file executables. "Both" means both apps include them.

| Package | Version | Licence | Copyright | Used by |
|---|---|---|---|---|
| [Terminal.Gui](https://github.com/tui-cs/Terminal.Gui) | 2.4.17 | MIT | Copyright 2007-2011 Novell Inc; Copyright 2017 Microsoft Corp | Both |
| [Terminal.Gui.Editor](https://github.com/tui-cs/Editor) | 2.5.7 | MIT | Copyright (c) gui.cs; tui-cs and contributors | Tedide |
| [TextMateSharp, TextMateSharp.Grammars](https://github.com/danipen/TextMateSharp) | 2.0.4 | MIT | Copyright (c) 2021 Daniel Peñalba | Both (via Terminal.Gui.Editor / Terminal.Gui) |
| [Onigwrap](https://github.com/aikawayataro/Onigwrap) | 1.0.11 | MIT | Copyright (c) 2024 Aikawa Yataro; Copyright (c) 2015 Fluent Solutions | Both (via TextMateSharp) |
| [Markdig](https://github.com/xoofx/markdig) | 1.3.2 | BSD-2-Clause | Copyright (c) 2016-2026, Alexandre Mutel | Both (via Terminal.Gui) |
| [ColorHelper](https://github.com/iamartyom/ColorHelper) | 1.8.1 | MIT | Copyright (c) 2020 Artyom Gritsuk | Both (via Terminal.Gui) |
| [Wcwidth](https://github.com/spectreconsole/wcwidth) | 4.0.1 | MIT | Copyright Patrik Svensson, Phil Scott | Both (via Terminal.Gui) |
| [System.IO.Abstractions, TestableIO.System.IO.Abstractions, TestableIO.System.IO.Abstractions.Wrappers](https://github.com/TestableIO/System.IO.Abstractions) | 22.1.1 | MIT | Copyright (c) Tatham Oddie and Contributors | Both (via Terminal.Gui) |
| [Testably.Abstractions.FileSystem.Interface](https://github.com/Testably/Testably.Abstractions) | 10.1.0 | MIT | Copyright (c) 2022 Valentin Breuß | Both (via System.IO.Abstractions) |
| [JetBrains.Annotations](https://github.com/JetBrains/JetBrains.Annotations) | 2026.2.0 | MIT | Copyright (c) 2016-2025 JetBrains s.r.o. | Both (via Terminal.Gui) |
| [Serilog](https://github.com/serilog/serilog) | 4.2.0 | Apache-2.0 | Copyright © Serilog Contributors | Both |
| [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file) | 6.0.0 | Apache-2.0 | Copyright © Serilog Contributors | Both |
| [Metalama.Framework](https://github.com/metalama/Metalama) (its `Metalama.Framework.dll` redistributable) | 2026.1.30 | MIT | Copyright 2020-2025, SharpCrafters s.r.o. and contributors | Both (the `[Log]` aspect) |
| [System.Drawing.Common](https://github.com/dotnet/winforms) (with System.Private.Windows.Core) | 9.0.0 | MIT | © Microsoft Corporation | Both (About dialog image) |
| [Microsoft.Win32.SystemEvents](https://github.com/dotnet/runtime) | 9.0.0 | MIT | © Microsoft Corporation | Both (via System.Drawing.Common) |
| [Microsoft.Extensions.Configuration, .Abstractions, .Binder, .FileExtensions, .Json; Microsoft.Extensions.DependencyInjection.Abstractions; Microsoft.Extensions.FileProviders.Abstractions, .Physical; Microsoft.Extensions.FileSystemGlobbing; Microsoft.Extensions.Logging.Abstractions; Microsoft.Extensions.Options; Microsoft.Extensions.Primitives](https://github.com/dotnet/dotnet) | 10.0.7 - 10.0.9 | MIT | © Microsoft Corporation | Both (via Terminal.Gui) |
| [Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core](https://github.com/dotnet/efcore) | 10.0.11 | MIT | © Microsoft Corporation | Doc Viewer |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) (bundle_e_sqlite3, core, provider.e_sqlite3, lib.e_sqlite3) | 2.1.12 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC | Doc Viewer (via Microsoft.Data.Sqlite) |

The standalone executables made by `dotnet publish --self-contained` also contain the
[.NET runtime](https://github.com/dotnet/runtime) (MIT, © .NET Foundation and Contributors), which
carries its own [third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT).

## Native code inside those libraries

| Component | Comes with | Licence | Copyright |
|---|---|---|---|
| [Oniguruma](https://github.com/kkos/oniguruma) regular expression library | Onigwrap's native `onigwrap` library | BSD-2-Clause | Copyright (c) 2002-2021 K.Kosako |
| [SQLite](https://sqlite.org/) | SQLitePCLRaw.lib.e_sqlite3's native `e_sqlite3` library | [Public domain](https://sqlite.org/copyright.html) | - |
| TextMate grammars and themes | Embedded in TextMateSharp.Grammars | Each under its own licence, mostly MIT, as collected from Visual Studio Code - see the [TextMateSharp](https://github.com/danipen/TextMateSharp) repository | Their respective authors |

## Source code included in Tedide

| What | Where | Licence | Copyright |
|---|---|---|---|
| cc65 runtime library routines, from `libsrc/runtime` at git b75f872 (cc65 2.19), changed only mechanically | `src/Tedide.Build/Opt6502/RuntimeHelpers.cs` (speed mode's inlined helpers) | zlib | Copyright (C) Ullrich von Bassewitz and the [cc65](https://github.com/cc65/cc65) contributors |

## Documentation bundled in the Doc Viewer

The Doc Viewer's `Docs.db` contains the following works, converted from HTML to Markdown by
`tools/Cc65DocsDbBuilder`. The HTML they were converted from is checked in under
`tools/Cc65DocsDbBuilder/SourceHtml/`. Each book also has its own licence or attribution page in
the Doc Viewer itself.

| Book | Source | Licence | Copyright and attribution |
|---|---|---|---|
| cc65 manuals | [cc65.github.io/doc](https://cc65.github.io/doc/) | zlib | Ullrich von Bassewitz and the cc65 contributors |
| Plus/4 memory map (in the cc65 book) | Generated from cc65's `_ted.h`, `_6551.h`, `plus4.h`, `cbm264.h` and `plus4.inc`, copied unchanged into `SourceHtml/Cc65Defs/` | zlib | (C) 1998-2006 Ullrich von Bassewitz and the cc65 contributors |
| The C Book (second edition) | [publications.gbdirect.co.uk/c_book](https://publications.gbdirect.co.uk/c_book/) | The book's own permission notice: anyone may reprint, redistribute or amend it, provided the original authorship and copyright are acknowledged with a link to [its copyright page](https://publications.gbdirect.co.uk/c_book/copyright.html). Offered with no warranty. | Copyright Mike Banahan, Declan Brady and Mark Doran, 1991; online version copyright Mike Banahan, 2003 |
| C64-Wiki articles (34) | [c64-wiki.com](https://www.c64-wiki.com/) | [GNU Free Documentation License 1.3](https://www.gnu.org/licenses/fdl-1.3.html) | The C64-Wiki authors; the Doc Viewer's `c64wiki/licence` page lists each article's source, revision and history |
| Wikipedia articles (13) | [en.wikipedia.org](https://en.wikipedia.org/) | [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/) | The Wikipedia contributors; the Doc Viewer's `wikipedia/licence` page lists each article's source, revision, history and the changes made |
| VICE manual (11 chapters) | [vice-emu.sourceforge.io](https://vice-emu.sourceforge.io/vice_toc.html) | [GNU GPL v2 or later](https://www.gnu.org/licenses/old-licenses/gpl-2.0.html) | The VICE Team; the manual's own Copyright and GNU GPL chapters are included in the Doc Viewer |

## Build-time and development-only tools

These are used to build or test Tedide but are not part of what it ships.

| Package | Version | Licence | Used for |
|---|---|---|---|
| [Metalama.Compiler](https://github.com/metalama/Metalama.Compiler), Metalama.Framework's compile-time part | 2026.1.20 / 2026.1.30 | MIT | Weaving the `[Log]` aspect at build time |
| [HtmlAgilityPack](https://github.com/zzzprojects/html-agility-pack) | 1.11.71 | MIT | `tools/Cc65DocsDbBuilder`, which builds `Docs.db` |
| [xunit](https://github.com/xunit/xunit), xunit.runner.visualstudio | 2.9.3 / 3.1.4 | Apache-2.0 | Unit tests |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 17.14.1 | MIT | Unit tests |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | 6.0.4 | MIT | Test coverage |

## Programs Tedide runs but does not include

Tedide starts these if they are installed, but does not include or redistribute them:

- [cc65](https://cc65.github.io/) (zlib) - the compiler, assembler, linker and archiver.
- [VICE](https://vice-emu.sourceforge.io/) (GPL v2 or later) - the emulators and their binary
  monitor, used for running and debugging.
- [Git](https://git-scm.com/) (GPL v2) - source control.

## Licence texts

### MIT License

Applies to Terminal.Gui, Terminal.Gui.Editor, TextMateSharp, Onigwrap, ColorHelper, Wcwidth,
System.IO.Abstractions, Testably.Abstractions, JetBrains.Annotations, Metalama, and the Microsoft
packages, with the copyright lines given in the tables above.

```text
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

### BSD 2-Clause License (Markdig)

```text
Copyright (c) 2016-2026, Alexandre Mutel
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### BSD 2-Clause License (Oniguruma)

```text
Copyright (c) 2002-2021  K.Kosako  <kkosako0@gmail.com>
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR AND CONTRIBUTORS ``AS IS'' AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED.  IN NO EVENT SHALL THE AUTHOR OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
SUCH DAMAGE.
```

### zlib License (cc65)

```text
This software is provided 'as-is', without any expressed or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not
   be misrepresented as being the original software.
3. This notice may not be removed or altered from any source
   distribution.
```

### Apache License 2.0

Applies to Serilog, Serilog.Sinks.File and SQLitePCLRaw (and, for development only, xunit).

```text

                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```
