﻿#------------------------------------------------------------------------------
# FILE:         build.ps1
# CONTRIBUTOR:  Jeff Lill
# COPYRIGHT:    Copyright (c) 2016-2018 by neonFORGE LLC.  All rights reserved.
#
# Builds the base [neon-log-collector] image.
#
# Usage: powershell -file build.ps1 REGISTRY TAG

param 
(
	[parameter(Mandatory=$True,Position=1)][string] $registry,
	[parameter(Mandatory=$True,Position=3)][string] $tag

)

#----------------------------------------------------------
# Global Includes
$image_root = "$env:NF_ROOT\\Images"
. $image_root/includes.ps1
#----------------------------------------------------------

"   "
"======================================="
"* neon-log-collector:" + $tag
"======================================="

# Build the image.
$maxmind_key = neon run -- cat "_...$src_images_path\neon-log-collector\maxmind"
Exec { docker build -t "${registry}:$tag" --build-arg "MAXMIND_KEY=$maxmind_key" . }
