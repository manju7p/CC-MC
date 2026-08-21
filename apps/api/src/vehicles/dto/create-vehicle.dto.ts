import { IsIn, IsInt, IsNumber, IsOptional, IsString, MinLength } from "class-validator";
import { RecordStatus } from "@cc-mc/shared-types";

export class CreateVehicleDto {
  @IsInt()
  centreId!: number;

  @IsString()
  @MinLength(1)
  vehicleNumber!: string;

  @IsOptional()
  @IsString()
  tankerNumber?: string;

  @IsOptional()
  @IsString()
  driverName?: string;

  @IsOptional()
  @IsString()
  driverMobile?: string;

  @IsOptional()
  @IsNumber()
  capacityKg?: number;

  @IsOptional()
  @IsIn([RecordStatus.ACTIVE, RecordStatus.INACTIVE])
  status?: RecordStatus;
}
