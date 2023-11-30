//-----------------------------------------------------------------------------
// FILE:        ThisAssembly.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:   Copyright © 2005-2023 by NEONFORGE LLC.  All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Reflection;

// $todo(jefflill):
//
// We can't use [GenerateAssemblyInfo=TRUE] to generate the assembly info because
// that can lead to duplicate symbol definitions (see [Directory.Build.props]).
//
// Instead, we're going to hardcode this here.  In the future, it would be nice
// to write a build task that generates this for us from the project properties.
//
// See [Directory.Build.props] for more information.

[assembly: AssemblyProduct("NEONKUBE")]
[assembly: AssemblyCompany("NEONFORGE LLC")]
[assembly: AssemblyCopyright("Copyright © 2005-2023 by NEONFORGE LLC.  All rights reserved.")]

#if DEBUG
[assembly: AssemblyConfiguration("Debug")]
#else
[assembly: AssemblyConfiguration("Release")]
#endif
