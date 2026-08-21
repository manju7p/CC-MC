import { IsIn, IsOptional, IsString } from "class-validator";
import { RecordStatus } from "@cc-mc/shared-types";

export class UpdateSourceDto {
  @IsOptional()
  @IsString()
  name?: string;

  @IsOptional()
  @IsString()
  location?: string;

  @IsOptional()
  @IsString()
  contact?: string;

  @IsOptional()
  @IsString()
  milkType?: string;

  @IsOptional()
  @IsIn([RecordStatus.ACTIVE, RecordStatus.INACTIVE])
  status?: RecordStatus;
}
