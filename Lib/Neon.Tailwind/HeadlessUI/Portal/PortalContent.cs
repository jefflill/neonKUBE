﻿using Microsoft.AspNetCore.Components;
//-----------------------------------------------------------------------------
// FILE:	    PortalContent.cs
// CONTRIBUTOR: Marcus Bowyer
// COPYRIGHT:  	Copyright (c) 2005-2022 by neonFORGE LLC.  All rights reserved.
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

using Microsoft.AspNetCore.Components.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Neon.Tailwind
{
    public class PortalContent : ComponentBase, IDisposable
    {
        [Inject] public IPortalBinder PortalBinder { get; set; }
        
        [Parameter] public RenderFragment ChildContent { get; set; }
        [Parameter] public string PortalName { get; set; } = "root";

        public void Dispose() => PortalBinder?.GetPortal(PortalName)?.RenderContent(builder => builder.AddContent(0, ""));

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var portal = PortalBinder?.GetPortal(PortalName);
            InvokeAsync(
                async () =>
                {
                    await Task.CompletedTask;
                    portal.RenderContent(ChildContent);
                });
        }
    }
}
