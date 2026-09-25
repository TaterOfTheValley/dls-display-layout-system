# Third-party notices

DLS (Display Layout System) bundles third-party components. All of them are MIT licensed, which imposes no
condition on how DLS itself is licensed — only that this copyright and permission
notice travels with the redistributed binary. That obligation is independent of
DLS's own licensing and applies however DLS is distributed. That is what this file
is for.

## Bundled in the self-contained build

The self-contained `DLS-<version>-win-x64.exe` embeds the .NET 8 runtime so it runs
on a machine with nothing installed. That means it redistributes:

| Component | Package | License |
|---|---|---|
| .NET runtime | `Microsoft.NETCore.App.Runtime.win-x64` | MIT |
| Windows Desktop runtime (Windows Forms) | `Microsoft.WindowsDesktop.App.Runtime.win-x64` | MIT |

The `-requires-dotnet8` build embeds neither — it calls the runtime the user has
already installed, so it redistributes nothing from this table.

## Bundled in both builds

| Component | Package | License |
|---|---|---|
| System.Text.Json | `System.Text.Json` 8.0.5 | MIT |
| Velopack updater and installer | `Velopack` 1.2.0 | MIT |

## MIT License

All components above are:

> Copyright (c) .NET Foundation and Contributors
> © Microsoft Corporation. All rights reserved.
> Copyright © Velopack Ltd. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify,
merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be included in all copies
or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE
OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The full upstream notices for the .NET runtime are published at
<https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>.
