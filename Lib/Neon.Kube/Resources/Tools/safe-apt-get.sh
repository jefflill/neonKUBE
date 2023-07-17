#!/bin/bash
#------------------------------------------------------------------------------
# FILE:         safe-apt-get
# CONTRIBUTOR:  Jeff Lill
# COPYRIGHT:    Copyright © 2005-2023 by NEONFORGE LLC.  All rights reserved.
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

# Wraps the [apt-get] command such that the command is retried for a
# while so it can acquire the lock.

set -e

LOCK_TIMEOUT_SECONDS=300

apt-get -o DPkg::Lock::Timeout=$LOCK_TIMEOUT_SECONDS "$@"
