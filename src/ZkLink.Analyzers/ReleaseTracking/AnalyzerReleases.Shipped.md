## Release 1.0.1

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
ZKL0001 | Security | Warning | Avoid literal digest passwords.
ZKL0002 | Security | Warning | Avoid explicit unsafe plaintext digest.
ZKL0003 | Usage | Warning | Retain and own watch results.
ZKL0004 | Usage | Warning | Observe asynchronous client and watch calls.
ZKL0005 | Usage | Warning | Pass an available CancellationToken.
ZKL0006 | Usage | Warning | Avoid sync-over-async on ZkLink calls.

## Release 1.1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
ZKL0007 | Usage | Warning | Retain state subscriptions returned by SubscribeStateChanges.
