﻿### Neon Desktop Service

NeonDesktop and neon-cli for Windows don't typically run with elevated permissions but both
of these need to be able to manage Hyper-V virtual machines and switches.  Unforunately,
these operations required elevated permissions.

To work around this, we're going to install a Neon Desktop Windows service that will run
with elevated permissions and is designed to handle these operations on behalf of the
clients via gRPC based operations.

The types in this namespace defined the gRPC messages as well as the Neon Desktop service 
definition.