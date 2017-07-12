#!/bin/sh
#------------------------------------------------------------------------------
# FILE:         docker-entrypoint.sh
# CONTRIBUTOR:  Jeff Lill
# COPYRIGHT:    Copyright (c) 2016-2017 by NeonForge, LLC.  All rights reserved.
#
# Loads the Docker host node environment variables before launching HAProxy.

# Load the Docker host node environment variables if present.

if [ -f /etc/neoncluster/env-host ] ; then
    . /etc/neoncluster/env-host
else

    # Initialize NEON_NODE_IP to the loopback address if host environment
    # variables weren't mapped.  We're doing this so the SYSLOG log line 
    # below will be valid when no [/etc/neoncluster/env-host] file is mounted.

    NEON_NODE_IP=127.0.0.1
fi

# Load the [/etc/neoncluster/env-container] environment variables if present.

if [ -f /etc/neoncluster/env-container ] ; then
    . /etc/neoncluster/env-container
fi

# Add the root directory to the PATH.

PATH=${PATH}:/

# Load the neonCLUSTER definitions.

. /neoncluster.sh

# Generate the static part of the HAProxy configuration file.  The config is
# pretty simple, some global defaults, the frontend definition followed by the
# backend which defines a server for each NAME:IP:PORT passed in VAULT_ENDPOINTS.
#
# Note that the [neon-log-collector] depends on the format of the proxy frontend
# and backend names, so don't change these.

mkdir -p /etc/haproxy
configPath=/etc/haproxy/haproxy.cfg

cat <<EOF > ${configPath}
#------------------------------------------------------------------------------
# FILE:         /etc/haproxy/haproxy.cfg
# CONTRIBUTOR:  Generated by the image [/docker-entrypoint.sh] script.

global

    # Maximum number of connections.  1K should be more than enough for
    # anything besides truly gigantic clusters.

    maxconn             1000

    # Randomize health checks some.

    spread-checks       5

    # Enable health checks via external scripts or programs.

    external-check

    # Enable logging to syslog on the local Docker host under the
    # NeonSysLogFacility_VaultLB facility.

    log                 ${NEON_NODE_IP}:${NeonHostPorts_LogHostSysLog} len 65535 ${NeonSysLogFacility_ProxyName}

defaults

    # Proxy inbound traffic as TCP so we can balance both HTTP and HTTPS
    # traffic without needing the TLS certificate.

    mode                tcp

    # Apply global log settings to services.

    log                 global

    # Log TCP connection details.

    option tcplog

    # Timeouts are relatively brief because Vault is cluster local and should be fast.

    timeout             connect 5s
    timeout             client 10s
    timeout             server 10s

    # Load balancing strategy.

    balance             roundrobin

    # Retry failed connections a couple of times (for a total of three attempts).

    retries             2

    # Amount of time after which a health check is considered to have timed out.

    timeout check       5s
    
# Proxy definitions.
#
# Note that [log-format] must be consistent with the standard format implemented
# by [NeonClusterHelper.GetProxyLogFormat()].

frontend tcp:vault-static
    bind                *:${NetworkPorts_Vault}
    unique-id-header    X-Activity-ID
    unique-id-format    ${NeonClusterConst_HAProxyUidFormat}
    log                 global
    log-format          "traffic�tcp-v1�neon-proxy-vault�%t�%ci�%b�%s�%si�%sp�%sslv�%sslc�%U�%B�%Tw�%Tc�%Tt�%ts�%ac�%fc�%bc�%sc�%rc�%sq�%bq"
    option              dontlognull
    default_backend     tcp:vault-static

backend tcp:vault-static
    option              log-health-checks
    option              external-check
    log                 global
    external-check      path "/usr/bin:/bin"
    external-check      command "/check-vault.sh"
EOF

# Process VAULT_ENDPOINTS by appending a server entry for each endpoint.
# Each entry written will look like:
#
#   server HOSTNAME IP:PORT

endpoints=$(echo ${VAULT_ENDPOINTS} | tr "," "\n")

for endpoint in ${endpoints}
do
    name=$(echo ${endpoint} | cut -d':' -f 1)
    ip=$(echo ${endpoint} | cut -d':' -f 2)
    port=$(echo ${endpoint} | cut -d':' -f 3)

    echo "    server              ${name}.${NeonHosts_Vault} ${ip}:${port} init-addr none check" >> ${configPath}
done

# Validate the configuration file and then launch HAProxy.

echo "[INFO] Verifying configuration."

if ! haproxy -c -q -f ${configPath} ; then
    echo "[FATAL] Invalid HAProxy configuration."
    exit 1
fi

# Launch HAProxy..

echo "[INFO] Starting HAProxy."

haproxy -f ${configPath}
