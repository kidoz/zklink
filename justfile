set positional-arguments

# List available shortcuts.
default:
    @just --list

# Restore the pinned local .NET tools.
tools:
    dotnet tool restore

# Restore tools and solution dependencies.
restore: tools
    dotnet restore ZkLink.slnx

# Build the solution (Release by default).
build configuration="Release":
    dotnet build ZkLink.slnx --configuration "$1"

# Run a selected test project through Microsoft.Testing.Platform.
test project:
    dotnet test --project "$1" --configuration Release

# Format C# and project XML with the pinned CSharpier tool.
format: tools
    dotnet csharpier format .

# Fail if C# or project XML needs formatting.
format-check: tools
    dotnet csharpier check .

# Check formatting and build the solution.
check: format-check build

# Validate the Compose configuration.
zk-check version="3.9.6":
    docker compose --env-file "dev/zookeeper-$1.env" config --quiet

# Start ZooKeeper and wait for authenticated readiness on all three nodes.
zk-up version="3.9.6":
    docker compose --env-file "dev/zookeeper-$1.env" up -d --wait --wait-timeout 90

# Show ZooKeeper container health and host ports.
zk-status version="3.9.6":
    docker compose --env-file "dev/zookeeper-$1.env" ps

# Print ZooKeeper logs; redirect to a file before cleanup when needed.
zk-logs version="3.9.6":
    docker compose --env-file "dev/zookeeper-$1.env" logs --no-color

# Remove the local test ensemble and its disposable data volumes.
zk-down version="3.9.6":
    docker compose --env-file "dev/zookeeper-$1.env" down --volumes

# Smoke-test both pinned ZooKeeper versions with isolated projects, logs, and cleanup.
zk-matrix:
    bash dev/test-zookeeper-matrix.sh

# Connect with the managed driver, authenticate, sync and close on every pinned server node.
zk-driver-matrix:
    bash dev/test-zookeeper-matrix.sh --driver

# Verify node, ACL, transaction, auxiliary and watch operations on both pinned versions.
zk-operations-matrix:
    bash dev/test-zookeeper-matrix.sh --operations

# Verify explicit TTL creation with enabled and disabled servers on both pinned versions.
zk-ttl-matrix:
    bash dev/test-zookeeper-matrix.sh --ttl

# Verify client TLS, mTLS, certificate rejection and secure recovery on both pinned versions.
zk-security-matrix:
    bash dev/test-zookeeper-security.sh

zk-security-concurrency:
    bash dev/test-zookeeper-security.sh --concurrent

# Stop one observed follower and verify same-session recovery on both pinned versions.
zk-follower-faults:
    bash dev/test-zookeeper-follower-faults.sh

# Stop the observed leader and verify election plus session/watch recovery on both versions.
zk-leader-faults:
    bash dev/test-zookeeper-leader-faults.sh

# Lose quorum and verify retained sessions or local loss on both pinned versions.
zk-quorum-faults:
    bash dev/test-zookeeper-quorum-faults.sh
