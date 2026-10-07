# TB Emergency Priority — Timberborn 1.1 standalone compatibility

This repository is an **independent compatibility and reverse-engineering project based on the Steam Workshop mod _Emergency Priority [ModContest1]_ by Staviette / grantemsley**.

- Workshop item: https://steamcommunity.com/sharedfiles/filedetails/?id=3729325910
- Original mod ID: `grantemsley.EmergencyPriority`
- Original Workshop version examined: `1.0.0.0`

The original design and original binary belong to the original mod author. This repository is not the original source tree and does not imply endorsement by the author.

## Standalone package

The installable package produced for this project is **one local mod**. It does not require the Workshop mod to be subscribed, installed, or enabled separately.

For local compatibility testing the package combines:

- the unmodified original Workshop assembly supplied by the user, to preserve the original game behavior;
- the original localization and thumbnail;
- a newly written `EmergencyPriority.V11Compat.dll`, whose source is under `StandaloneCompat/`;
- a Timberborn 1.1 manifest preserving the original mod ID so integrations and existing saves continue to recognize Emergency Priority.

The original Workshop DLL is **not committed to this repository**.

## Full reverse-engineering audit

The supplied Workshop DLL was inspected at .NET metadata and IL level. The functional surface includes the Emergency construction state/registry, UI toggle integration, builder job selection, worker schedule override, sleep override, need selection, carrying behavior, and immediate worker interruption.

The original mod applies Harmony patches to these Timberborn methods:

- `BeaverNeedBehaviorPicker.ShouldPickEssentialAction`
- `BuilderHubWorkplaceBehavior.Decide`
- `BuilderPriorityToggleGroupFactory.Create`
- `CarryRootBehavior.Decide`
- `DistrictNeedBehaviorService.PickShortestAction`
- `PriorityToggleGroup.Enable`
- `PriorityToggleGroup.Disable`
- `PriorityToggleGroup.UpdateGroup`
- `PriorityToggle.OnValueChanged`
- `SleepNeedBehavior.ShouldSleepAtHome`
- `SleepNeedBehavior.Decide`
- `SleepNeedBehavior.SleepOutside`
- `WorkerRootBehavior.Decide`

### What each subsystem does

**Emergency state and persistence**

`EmergencyConstructable` stores the Emergency flag as persistent construction-site state and registers/unregisters unfinished Emergency jobs with `EmergencyConstructionRegistry`.

**Builder selection**

Emergency jobs are considered ahead of ordinary builder work. Reachability is checked from the Builder Hub before starting a construction job.

**Immediate interruption**

When a new Emergency job is registered, assigned builders are inspected. The original `TryInterrupt` can terminate a running need action, wake a sleeping builder, or stop an unrelated hauling action so the builder can immediately reconsider work.

**Schedule override**

Emergency builders can continue working outside their normal work schedule, while still respecting hard work refusal conditions.

**Sleep override**

Emergency builders avoid normal sleep/home behavior while an Emergency job exists. Once sleep becomes critical, they are allowed to sleep on the spot rather than travelling home.

**Critical needs**

Emergency work does not make beavers ignore survival. At critical need levels, the mod selects a nearby viable essential action rather than ordinary preference-based need handling.

**Hauling protection**

A builder already hauling materials to an Emergency construction site is not interrupted.

**UI**

An Emergency toggle is inserted alongside ordinary construction priority controls. Selecting an ordinary priority clears Emergency. The Emergency state is shown in red and has its own tooltip.

## Confirmed Timberborn 1.1 incompatibility

The original 1.0 interruption path directly calls:

`GoodCarrier.CarriedGoods`

That member no longer matches Timberborn 1.1's carrying API. The observed result is:

```
MissingMethodException:
Method not found:
Timberborn.Goods.GoodAmount Timberborn.Carrying.GoodCarrier.get_CarriedGoods()
```

The earlier local compatibility DLL changed only two bytes in the original assembly. That bypass attempt left the method body with invalid stack/control-flow IL and later produced:

```
InvalidProgramException:
Invalid IL code in EmergencyInterruptionService.TryInterrupt(...)
```

That binary edit is not used by the new compatibility layer.

## 1.1 fix

The original `TryInterrupt` has exactly one caller in the original assembly: `EmergencyInterruptionService.OnJobRegistered`.

The standalone compatibility assembly therefore patches the caller and redirects that one invocation to a newly written, valid 1.1 implementation. The broken original method is never executed.

The replacement:

- supports Timberborn 1.1's `GoodCarrier.CarriedGood.GoodAmount` shape;
- keeps a legacy `CarriedGoods` fallback for diagnostics;
- preserves recovered-good spawning before emptying a carrier;
- preserves stock/capacity reservation release;
- refuses to interrupt a haul whose destination is already an Emergency site;
- preflights cargo/reservation operations before mutation;
- fails safe if a future game update moves another API: the current worker task is left intact rather than risking lost goods or half-released reservations;
- logs one-time compatibility warnings instead of crashing the game.

## Other compatibility risks found

The rest of the original mod is substantially more defensive than the cargo path. Several features reflect private Timberborn fields/methods and already disable only the affected feature if the reflected member cannot be found. Fragile hooks include:

- `BehaviorManager._runningExecutor` / `_runningBehavior`
- `ApplyEffectExecutor._finishTimestamp`
- `BuilderHubWorkplaceBehavior._accessible`
- `ConstructionJob._constructionSiteAccessible`
- `PriorityToggle._prioritizable`
- `SleepNeedBehavior._walkedToSleepingPosition`
- `WorkerRootBehavior._worker`, `_workRefuser`, and `DecideAsWorker`
- internal need-appraisal fields/members
- private UI factory/loader fields

No second hard 1.1 crash was demonstrated in the supplied logs. The standalone compatibility assembly probes the most important interruption/carrying members at startup and writes results to `EmergencyPriority_1.1_Compat.log`. This gives one-run evidence if a later Timberborn build changes another internal API.

## Source layout

- `StandaloneCompat/` — source for the standalone Timberborn 1.1 compatibility assembly.
- `EmergencyPriorityCompatAudit/` — earlier diagnostic/audit experiment retained for history; not required by the standalone package.
- `.github/workflows/build-standalone-compat.yml` — clean CI build of the compatibility assembly.

## Installation

Use the generated standalone ZIP as a local Timberborn mod. Do not enable the original Workshop copy or the old two-byte `EmergencyPriority_1.1.2.4_Compat` at the same time.
