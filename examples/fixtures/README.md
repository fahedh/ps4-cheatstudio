# Synthetic porting fixtures

These deterministic byte arrays are artificial and contain no memory dump from a real console. They do not implement real health behavior. The single-line cases use the owner-supplied v01.04 bytes and offset as reference metadata. The multi-line case uses synthetic source addresses 0x100/0x200 to verify relocated cave jumps and dependency order.

Cases: unchanged, relocated, ambiguous, changed instruction, invalid cave, missing signature, and multi-line relative jumps. Automated tests assert both candidate counts and whether a testable proposal is allowed. No candidate earns hardware-verified status.
