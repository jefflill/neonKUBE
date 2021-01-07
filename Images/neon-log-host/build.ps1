﻿#------------------------------------------------------------------------------
# FILE:         build.ps1
# CONTRIBUTOR:  Marcus Bowyer
# COPYRIGHT:    Copyright (c) 2005-2021 by neonFORGE LLC.  All rights reserved.
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

# Builds the Neon Log Host image.
#
# Usage: powershell -file build.ps1 REGISTRY UBUNTU_TAG TAG

param 
(
	[parameter(Mandatory=$true,Position=1)][string] $registry,
	[parameter(Mandatory=$true,Position=2)][string] $baseTag,
	[parameter(Mandatory=$true,Position=3)][string] $tag
)

Log-ImageBuild $registry $tag

$organization = DockerOrg

# Build the image.
$maxmind_key = neon run -- cat "_...$src_images_path\neon-log-collector\maxmind"
Exec { docker build -t "${registry}:$tag" --build-arg "ORGANIZATION=$organization" --build-arg "CLUSTER_VERSION=neonkube-$neonKUBE_Version" --build-arg "BASE_TAG=$baseTag" --build-arg "MAXMIND_KEY=$maxmind_key" . }
