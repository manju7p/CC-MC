import type { Device } from "../device/device.types";
import type { AnalyserReading, ScaleReading } from "../normalization/normalized-reading.types";
import { generateLocalIdempotencyKey } from "../storage/idempotency";
import type { CreateLocalTransactionInput, CreateLocalTransactionResult } from "../storage/local-transaction.types";
import type { LocalStorage } from "../storage/storage.types";
import { assembleReception } from "./reception-validation";

export interface CaptureContext {
  centreId: number;
  sourceId: number;
  vehicleId: number;
}

/**
 * The application-level workflow that turns "read the devices" into "a
 * durable, idempotent local transaction." This is the piece Checkpoint
 * 4's instructions call for explicitly: "If the exact device interaction
 * sequence is unknown, represent it explicitly as an application-level
 * workflow abstraction and document the assumption" - rather than baking
 * an invented timing/sequencing assumption into the Device interface or
 * the simulators themselves, it lives here, named and documented, so it
 * can be revisited without touching device or storage code.
 *
 * TWO DOCUMENTED, REVISABLE ASSUMPTIONS (neither is BRD-confirmed or
 * hardware-confirmed - both are the simplest reasonable choice for a
 * gateway with no real device timing spec yet):
 *
 * 1. CONCURRENT reads (Promise.all), not sequential. The BRD does not
 *    specify whether an operator's workflow puts milk on the scale and
 *    starts the analyser at the same time, one after the other, or
 *    whether the two devices are even read as part of one operator
 *    action at all. Reading them concurrently is the assumption with the
 *    fewest hidden ordering requirements; if real devices need e.g.
 *    "must weigh only after the analyser confirms a stable sample," that
 *    sequencing belongs here (readAndAssemble is the one place a
 *    sequencing rule would be added), not in the Device interface.
 *
 * 2. capturedAt = assembly time (wall-clock when both readings have
 *    returned), NOT either device reading's own capturedAt. Both
 *    ScaleReading and AnalyserReading carry their own capturedAt today,
 *    but nothing establishes which (if either) should become the
 *    transaction's canonical capturedAt when they differ by however many
 *    seconds separate two physical reads, or what "the reception was
 *    captured at" even means for two readings taken at different
 *    moments. Assembly time is a single, unambiguous, always-available
 *    value that doesn't require inventing a reconciliation rule for two
 *    timestamps the BRD never asked the gateway to reconcile.
 *
 * IDEMPOTENCY-KEY-ONCE DESIGN: split into readAndAssemble() (reads
 * devices, validates, generates the key) and persist() (writes to
 * LocalStorage) specifically so a caller that needs to retry ONLY the
 * persistence step (e.g. the initial createLocalTransaction() call threw
 * for a transient reason) can call persist() again with the SAME
 * CreateLocalTransactionInput - without re-reading devices and, critically,
 * WITHOUT calling generateLocalIdempotencyKey() again. captureReception()
 * is a convenience wrapper for the common case where no such split is
 * needed.
 */
export class ReceptionCaptureWorkflow {
  constructor(
    private readonly scale: Device<ScaleReading>,
    private readonly analyser: Device<AnalyserReading>,
    private readonly storage: LocalStorage,
    private readonly gatewayId: string,
    private readonly now: () => string = () => new Date().toISOString(),
  ) {}

  /**
   * Reads both devices, validates, and generates the idempotency key -
   * but does NOT touch LocalStorage. Nothing is persisted by this call.
   *
   * Failure semantics (see docs/gateway-architecture.md and the
   * checkpoint's "IMPORTANT FAILURE SEMANTICS" list):
   *  - Either device's read() rejects (not connected, sensor fault) ->
   *    this rejects too, propagating the device's error unchanged. No
   *    idempotency key is generated, no transaction is created. (#1)
   *  - Both reads succeed but assembleReception() finds the combined
   *    data invalid (e.g. a negative quantity) -> this rejects with
   *    InvalidReadingError. No idempotency key is generated. (#2)
   *  - Only once validation has fully passed does this generate the
   *    localIdempotencyKey - exactly once, here, per logical capture
   *    attempt.
   */
  async readAndAssemble(context: CaptureContext): Promise<CreateLocalTransactionInput> {
    const [scaleReading, analyserReading] = await Promise.all([this.scale.read(), this.analyser.read()]);
    const assembledAt = this.now();

    const assembled = assembleReception({
      centreId: context.centreId,
      sourceId: context.sourceId,
      vehicleId: context.vehicleId,
      scaleReading,
      analyserReading,
      assembledAt,
    });

    return { ...assembled, localIdempotencyKey: generateLocalIdempotencyKey(this.gatewayId) };
  }

  /**
   * Persists an already-assembled, already-keyed input. Safe to call
   * more than once with the SAME input (e.g. retrying after a transient
   * storage failure) - LocalStorage.createLocalTransaction() is itself
   * idempotent on localIdempotencyKey (see storage.types.ts), so a retry
   * here reaches the same durable state without a duplicate row and
   * without this method ever generating a new key.
   */
  async persist(input: CreateLocalTransactionInput): Promise<CreateLocalTransactionResult> {
    return this.storage.createLocalTransaction(input);
  }

  /**
   * Convenience wrapper: readAndAssemble() then persist(), for the
   * common case where a caller doesn't need to separate the two phases.
   * Equivalent to `workflow.persist(await workflow.readAndAssemble(context))`.
   */
  async captureReception(context: CaptureContext): Promise<CreateLocalTransactionResult> {
    const input = await this.readAndAssemble(context);
    return this.persist(input);
  }
}
