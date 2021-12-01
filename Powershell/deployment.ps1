#Requires -Version 7.1.3 -RunAsAdministrator
#------------------------------------------------------------------------------
# FILE:         deployment.ps1
# CONTRIBUTOR:  Jeff Lill
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

#------------------------------------------------------------------------------
# IMPORTANT:
#
# This file defines GitHub related Powershell functions and is intended for use
# in GitHub actions and other deployment related scenarios.  This file is intended
# to be shared/included across multiple GitHub repos and should never include
# repo-specific code.
#
# After modifying this file, you should take care to push any changes to the
# other repos where this file is present.

$scriptPath   = $MyInvocation.MyCommand.Path
$scriptFolder = [System.IO.Path]::GetDirectoryName($scriptPath)

Push-Location $scriptFolder | Out-Null

. ./error-handling.ps1
. ./utility.ps1

Pop-Location | Out-Null

# Load these assemblies from the [neon-assistant] installation folder
# to ensure we'll be compatible.

Load-Assembly "$env:NEON_ASSISTANT_HOME\YamlDotNet.dll"
Load-Assembly "$env:NEON_ASSISTANT_HOME\Neon.Common.dll"
Load-Assembly "$env:NEON_ASSISTANT_HOME\Neon.Deployment.dll"

#------------------------------------------------------------------------------
# Returns a global [Neon.Deployment.ProfileClient] instance creating one if necessary.
# This can be used to query the [neon-assistant] installed on the workstation for
# secret passwords, secret values, as well as profile values.  The client is thread-safe,
# can be used multiple times, and does not need to be disposed.

$global:__neonProfileClient = $null

function Get-ProfileClient
{
    if ($null -ne $global:__neonProfileClient)
    {
        return $global:__neonProfileClient
    }

    $global:__neonProfileClient = New-Object "Neon.Deployment.ProfileClient"

    return $global:__neonProfileClient
}

#------------------------------------------------------------------------------
# Returns the named profile value.
#
# ARGUMENTS:
#
#   name            - Specifies the profile value name
#   $nullOnNotFound - Optionally specifies that $null should be returned rather 
#                     than throwing an exception when the profile value does 
#                     not exist.

function Get-ProfileValue
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$true)]
        [string]$name,
        [Parameter(Position=1, Mandatory=$false)]
        [bool]$nullOnNotFound = $false
    )

    $client = Get-ProfileClient

    return $client.GetProfileValue($name, $nullOnNotFound)
}

#------------------------------------------------------------------------------
# Returns the named secret password, optionally specifying a non-default vault.
#
# ARGUMENTS:
#
#   name            - Specifies the secret password name
#   vault           - Optionally overrides the default vault
#   masterPassword  - Optionally specifies the master 1Password (for automation)
#   $nullOnNotFound - Optionally specifies that $null should be returned rather 
#                     than throwing an exception when the secret does not exist.

function Get-SecretPassword
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$true)]
        [string]$name,
        [Parameter(Position=1, Mandatory=$false)]
        [string]$vault = $null,
        [Parameter(Position=2, Mandatory=$false)]
        [string]$masterPassword = $null,
        [Parameter(Position=3, Mandatory=$false)]
        [bool]$nullOnNotFound = $false
    )

    $client = Get-ProfileClient

    return $client.GetSecretPassword($name, $vault, $masterPassword, $nullOnNotFound)
}

#------------------------------------------------------------------------------
# Returns the named secret value, optionally specifying a non-default vault.
#
# ARGUMENTS:
#
#   name            - Specifies the secret value name
#   vault           - Optionally overrides the default vault
#   masterPassword  - Optionally specifies the master 1Password (for automation)
#   $nullOnNotFound - Optionally specifies that $null should be returned rather 
#                     than throwing an exception when the secret does not exist.

function Get-SecretValue
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$true)]
        [string]$name,
        [Parameter(Position=1, Mandatory=$false)]
        [string]$vault = $null,
        [Parameter(Position=2, Mandatory=$false)]
        [string]$masterPassword = $null,
        [Parameter(Position=3, Mandatory=$false)]
        [bool]$nullOnNotFound = $false
    )

    $client = Get-ProfileClient

    return $client.GetSecretValue($name, $vault, $masterPassword, $nullOnNotFound)
}

#------------------------------------------------------------------------------
# Retrieves the AWS access key ID and secret access key from from 1Password 
# and sets these enviroment variables for use by the AWS-CLI:
#
#   AWS_ACCESS_KEY_ID
#   AWS_SECRET_ACCESS_KEY
#
# ARGUMENTS:
#
#   awsAccessKeyId      - Optionally overrides the key ID password name
#   awsSecretAccessKey  - Optionally overrides the secret key password name
#   vault               - Optionally overrides the default vault
#   masterPassword      - Optionally specifies the master 1Password (for automation)

function Import-AwsCliCredentials
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$false)]
        [string]$awsAccessKeyId = "AWS_ACCESS_KEY_ID",
        [Parameter(Position=1, Mandatory=$false)]
        [string]$awsSecretAccessKey = "AWS_SECRET_ACCESS_KEY",
        [Parameter(Position=1, Mandatory=$false)]
        [string]$vault = $null,
        [Parameter(Position=2, Mandatory=$false)]
        [string]$masterPassword = $null
    )

    if (![System.String]::IsNullOrEmpty($env:AWS_ACCESS_KEY_ID) -and ![System.String]::IsNullOrEmpty($env:AWS_SECRET_ACCESS_KEY))
    {
        return  # Already set
    }

    $client = Get-ProfileClient

    $env:AWS_ACCESS_KEY_ID     = $client.GetSecretPassword($awsAccessKeyId, $vault, $masterPassword)
    $env:AWS_SECRET_ACCESS_KEY = $client.GetSecretPassword($awsSecretAccessKey, $vault, $masterPassword)
}

#------------------------------------------------------------------------------
# Removes the AWS-CLI credential environment variables if present:
#
#   AWS_ACCESS_KEY_ID
#   AWS_SECRET_ACCESS_KEY

function Remove-AwsCliCredentials
{
    $env:AWS_ACCESS_KEY_ID     = $null
    $env:AWS_SECRET_ACCESS_KEY = $null
}

#------------------------------------------------------------------------------
# Retrieves the GITHUB_PAT (personal access token) from from 1Password 
# and sets the GITHUB_PAT environment variable used by the GitHub-CLI
# as well as the [Neon.Deployment.GitHub] class.
#
# ARGUMENTS:
#
#   name            - Optionally overrides the default secret name (GITHUB_PAT)
#   vault           - Optionally overrides the default vault
#   masterPassword  - Optionally specifies the master 1Password (for automation)

function Import-GitHubCredentials
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$false)]
        [string]$name = "GITHUB_PAT",
        [Parameter(Position=1, Mandatory=$false)]
        [string]$vault = $null,
        [Parameter(Position=2, Mandatory=$false)]
        [string]$masterPassword = $null
    )

    if (![System.String]::IsNullOrEmpty($env:GITHUB_PAT))
    {
        return  # Already set
    }

    $client = Get-ProfileClient

    $env:GITHUB_PAT = $client.GetSecretPassword($name, $vault, $masterPassword)
}

#------------------------------------------------------------------------------
# Retrieves common CI/CD related secrets from 1Password and sets the corresponding
# environment variables.  This currently loads the AWS and GitHub related credentials.
#
# ARGUMENTS: NONE

function Import-CommonCredentials
{
    Import-AwsCliCredentials
    Import-GitHubCredentials
}

#------------------------------------------------------------------------------
# Removes the GitHub credential environment variables if present:
#
#   GITHUB_PAT

function Remove-GitHubCredentials
{
    $env:GITHUB_PAT = $null
}

#------------------------------------------------------------------------------
# Deletes a GitHub container image, optionally using filesystem-style wildcards
# "*" and "?".  This uses the current user's GITHUB_PAT as credentials.
#
# ARGUMENTS:
#
#   organization    - specifies the GitHib organization hosting the registry
#   name            - the container image name (with optional wildcards)

function Remove-GitHub-Container
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$true)]
        [string]$organization,
        [Parameter(Position=1, Mandatory=$true)]
        [string]$nameOrPattern
    )

    [Neon.Deployment.GitHub]::Packages.Delete($organization, $nameOrPattern, "container")    
}

#------------------------------------------------------------------------------
# Changes a GitHub container image visibility, optionally using filesystem-style
# wildcards "*" and "?".  This uses the current user's GITHUB_PAT as credentials.
#
# ARGUMENTS:
#
#   organization    - specifies the GitHib organization hosting the registry
#   name            - the container image name (with optional wildcards)
#   visibility      - the new visibility, one of:
#
#                           all, public, private, or internal

function Set-GitHub-Container-Visibility
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$true)]
        [string]$organization,
        [Parameter(Position=1, Mandatory=$true)]
        [string]$nameOrPattern,
        [Parameter(Position=2, Mandatory=$true)]
        [string]$visibility
    )

    $visibility = [Neon.Deployment.GitHubPackageVisibility]$visibility

    [Neon.Deployment.GitHub]::Packages.SetVisibility($organization, $nameOrPattern, "container", $visibility)
}

#------------------------------------------------------------------------------
# Determines whether a XenServer/XCP-ng host machine is running by connecting
# to it.
#
# ARGUMENTS:
# 
#   addressOrFQDN   - the IP address or fully qualified domain name for the 
#                     target XenServer-XCP-ng host machine
#
#   username        - the username for the host (generally [root])
#
#   password        - the user password
#
# RETURNS:
#
#   $true when the host machine is running.

function Check-XenServer
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$true)]
        [string]$addressOrFQDN,
        [Parameter(Position=1, Mandatory=$true)]
        [string]$username,
        [Parameter(Position=2, Mandatory=$true)]
        [string]$password
    )

    return [Neon.Deployment.XenServer]::IsRunning($addressOrFQDN, $username, $password)
}

#------------------------------------------------------------------------------
# Connects to a XenServer/XCP-ng host and removes any VMs matching the name or file
# wildcard pattern, forceably shutting the VMs down when necessary.  Note that the
# VM's drives will also be removed.
#
# ARGUMENTS:
#
#   addressOrFQDN   - the IP address or fully qualified domain name for the 
#                     target XenServer-XCP-ng host machine
#
#   username        - the username for the host (generally [root])
#
#   password        - the user password
#
#   nameOrPattern   - the name of a specific VM to be deleted or a pattern including
#                     [*] and/or [?] wildcards to match the names of VMs to be deleted

function Remove-XenServerVMs
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$true)]
        [string]$addressOrFQDN,
        [Parameter(Position=1, Mandatory=$true)]
        [string]$username,
        [Parameter(Position=2, Mandatory=$true)]
        [string]$password,
        [Parameter(Position=3, Mandatory=$true)]
        [string]$nameOrPattern
    )

    [Neon.Deployment.XenServer]::RemoveVMs($addressOrFQDN, $username, $password, $nameOrPattern)
}

#------------------------------------------------------------------------------
# Removes the Powershell command history on the current machine.  This is useful
# for ensuring the sensitive information like credentials that leak into the
# history can be removed.

function Clear-PowershellHistory
{
    [Neon.Deployment.DeploymentHelper]::ClearPowershellHistory()
}

#------------------------------------------------------------------------------
# Uploads a file from the local workstation to S3.
#
# ARGUMENTS:
#
#   sourcePath          - path to the file being uploaded
#
#   targetUri           - the target S3 URI This may be either an [s3://BUCKET/KEY] or a
#                         https://s3.REGION.amazonaws.com/BUCKET/KEY URI referencing an S3 
#                         bucket and key.
#
#   gzip                - Optionally indicates that the target content encoding should be set to [gzip]
#
#   metadata            - Optionally specifies HTTP metadata headers to be returned when the object
#                         is downloaded from S3.  This formatted as as comma separated a list of 
#                         key/value pairs like:
#        
#                               Content-Type=text,app-version=1.0.0
#
#                         AWS supports [system] as well as [custom] headers.  System headers
#                         include standard HTTP headers such as [Content-Type] and [Content-Encoding].
#                         Custom headers are required to include the <b>x-amz-meta-</b> prefix.
#
#                         You don't need to specify the [x-amz-meta-] prefix for setting custom 
#                         headers; the AWS-CLI detects custom header names and adds the prefix automatically. 
#                         This method will strip the prefix if present before calling the AWS-CLI to ensure 
#                         the prefix doesn't end up being duplicated.
#
#   publicReadAccess    - Optionally indicates that the upload file will allow read-only access to the world.

function Save-ToS3
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$true)]
        [string]$sourcePath,
        [Parameter(Position=1, Mandatory=$true)]
        [string]$targetUri,
        [Parameter(Mandatory=$false)]
        [bool]$gzip = $false,
        [Parameter(Mandatory=$false)]
        [string]$metadata = "",
        [Parameter(Mandatory=$false)]
        [bool]$publicReadAccess = $false
    )

    [Neon.Deployment.AwsCli]::S3Upload($sourcePath, $targetUri, $gzip, $metedata, $publicReadAccess)
}

#------------------------------------------------------------------------------
# Downloads a file from S3.
#
# ARGMENTS:
#
#   sourceUri           - Identifies the S3 object to download.  This may be an <b>s3://BUCKET/KEY</b>
#                         or a https://s3.REGION.amazonaws.com/BUCKET/KEY URI referencing an S3 
#                         bucket and key.
#
#   targetPath          - Specifies where the downloaded file will be written

function Get-FromS3
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$true)]
        [string]$sourceUri,
        [Parameter(Position=1, Mandatory=$true)]
        [string]$targetPath
    )

    [Neon.Deployment.AwsCli]::S3Download($sourceUri, $targetPath)
}

#------------------------------------------------------------------------------
# Removes one or more S3 objects.
#
# ARGUMENTS:
#
#   targetUri           - Identifies target S3 URI or prefix for the object(s) to be removed.  This may be either an
#                         [s3://BUCKET[/KEY]] or a [https://s3.REGION.amazonaws.com/BUCKET[/KEY]] URI 
#                         referencing an S3 bucket and key.  Note that the key is optional which means that all
#                         objects in the bucket are eligible for removal.
#
#   recursive           - Optionally indicates that [targetUri] specifies a folder prefix and that
#                         all objects within the folder are eligble for removal. 
#
#   include             - Optionally specifies a pattern specifying the objects to be removed.
#
#   exclude             - Optionally specifies a pattern specifying objects to be excluded from removal.

function Remove-FromS3
{
    [CmdletBinding()]
    param (
        [Parameter(Position=0, Mandatory=$true)]
        [string]$targetUri,
        [Parameter(Mandatory=$false)]
        [bool]$recursive = $false,
        [Parameter(Mandatory=$false)]
        [string]$include = "",
        [Parameter(Mandatory=$false)]
        [string]$exclude = ""
    )

    [Neon.Deployment.AwsCli]::S3Remove($targetUri, $recursive, $include, $exclude)
}
