import { IsIn, IsNumber, IsOptional, IsString } from "class-validator";
import { RecordStatus } from "@cc-mc/shared-types";

export class UpdateVehicleDto {
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
