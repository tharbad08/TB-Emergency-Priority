# TB Emergency Priority — Timberborn 1.1 compatibility & reverse-engineering work

This repository is an **independent compatibility/reverse-engineering project based on the Steam Workshop mod _Emergency Priority [ModContest1]_ by Staviette**:

- Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3729325910
- Original mod ID: `grantemsley.EmergencyPriority`
- Original Workshop version observed: `1.0.0.0`
- Original dependency: Harmony

The original Workshop mod and its design belong to its original author. This repository is **not the original source code** and does not imply endorsement by Staviette. The source here is newly written compatibility/audit code based on observed runtime behavior, public Workshop documentation, and Timberborn logs.

## Why this exists

The Workshop release targets Timberborn 1.0. Timberborn 1.1 changed APIs used by the mod.

A confirmed 1.1 crash is:

```
MissingMethodException:
Method not found:
Timberborn.Goods.GoodAmount Timberborn.Carrying.GoodCarrier.get_CarriedGoods()

grantemsley.EmergencyPriority.EmergencyInterruptionService.TryInterrupt(...)
```

A previous binary two-byte workaround avoided that old member reference but left `TryInterrupt` with invalid IL and later caused:

```
InvalidProgramException:
Invalid IL code in
grantemsley.EmergencyPriority.EmergencyInterruptionService.TryInterrupt(...)
```

This repository deliberately avoids binary IL surgery.

## Current approach

`Emergency Priority 1.1 Compatibility Audit` is a **separate mod**. It expects the original Workshop mod to be installed.

On startup it:

1. Locates the original `grantemsley.EmergencyPriority` assembly.
2. Patches the caller of the broken `TryInterrupt` method so the malformed method is never JIT-compiled.
3. Preserves the original Emergency registration / priority flow while temporarily skipping only the immediate "drop current job" step.
4. Walks **every type and every method body** in the original assembly.
5. Resolves metadata references used by the IL and records unresolved methods, fields, and types.
6. Dumps the original mod's type/method/property/field surface to an audit log.

The audit file is written next to this mod as:

`emergency-priority-compat-audit.log`

That gives us one game run that can expose other Timberborn 1.0 → 1.1 API breaks instead of finding them one crash at a time.

## Known behavior that must ultimately be preserved

The Workshop description says Emergency Priority provides four distinct behaviors for builders:

- Emergency construction outranks normal construction.
- Builders can abandon their current job and route to an Emergency construction site.
- Emergency builders keep working outside their normal shift and can sleep at the worksite.
- When hunger/thirst becomes critical they use the closest viable food/water source instead of their preferred source.

The compatibility work treats these as separate subsystems and audits all of them.

## Important

Do **not** enable the old locally binary-edited `EmergencyPriority_1.1.2.4_Compat` at the same time. Use the unmodified Workshop release plus this compatibility mod.

The immediate compatibility patch intentionally does **not** reproduce the cargo-dropping/interruption code yet. That logic depended on the removed `GoodCarrier.CarriedGoods` API and needs to be rebuilt against the current 1.1 worker/carrying API rather than guessed.
