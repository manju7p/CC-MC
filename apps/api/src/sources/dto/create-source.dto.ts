import { IsIn, IsInt, IsOptional, IsString, MinLength } from "class-validator";
import { RecordStatus } from "@cc-mc/shared-types";

export class CreateSourceDto {
  @IsInt()
  centreId!: number;

  @IsString()
  @MinLength(1)
  code!: string;

  @IsString()
  @MinLength(1)
  name!: string;

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
