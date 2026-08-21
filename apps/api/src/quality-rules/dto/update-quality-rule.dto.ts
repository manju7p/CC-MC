import { IsNumber } from "class-validator";

export class UpdateQualityRuleDto {
  @IsNumber()
  minValue!: number;

  @IsNumber()
  maxValue!: number;
}
