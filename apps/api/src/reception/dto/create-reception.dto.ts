import { IsInt, IsNumber, Min } from "class-validator";

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
}
