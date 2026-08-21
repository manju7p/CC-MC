import { IsIn, IsString, MinLength } from "class-validator";
import { TransactionStatus } from "@cc-mc/shared-types";

export class OverrideReceptionDto {
  @IsIn([TransactionStatus.ACCEPTED, TransactionStatus.REJECTED])
  newStatus!: TransactionStatus.ACCEPTED | TransactionStatus.REJECTED;

  @IsString()
  @MinLength(3)
  reason!: string;
}
