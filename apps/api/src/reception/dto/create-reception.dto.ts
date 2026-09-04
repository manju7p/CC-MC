import { IsInt, IsNumber, IsOptional, IsString, Min, MinLength } from "class-validator";

export class CreateReceptionDto {
  @IsInt()
  centreId!: number;

  @IsInt()
  sourceId!: number;

  @IsInt()
  vehicleId!: number;

  @IsNumber()
  @Min(0)
  quantityKg!: number;

  @IsNumber()
  @Min(0)
  fat!: number;

  @IsNumber()
  @Min(0)
  snf!: number;

  @IsNumber()
  temperature!: number;

  /**
   * Optional, additive (Checkpoint 5) - see CreateReceptionRequest in
   * @cc-mc/shared-types for the full semantics. Omitted by the existing
   * web create-reception form; the Local Device Gateway always sets it.
   * A caller that sends this field is opting into idempotent-retry
   * semantics for this create call - see ReceptionService.create().
   */
  @IsOptional()
  @IsString()
  @MinLength(1)
  localIdempotencyKey?: string;
}
