import { Column, CreateDateColumn, Entity, JoinColumn, ManyToOne, PrimaryGeneratedColumn } from "typeorm";
import { TransactionStatus } from "@cc-mc/shared-types";
import { MilkReceptionTransaction } from "./milk-reception-transaction.entity";
import { User } from "../../auth/entities/user.entity";

@Entity("transaction_overrides")
export class TransactionOverride {
  @PrimaryGeneratedColumn()
  id!: number;

  @Column()
  transactionId!: number;

  @Column({ type: "enum", enum: TransactionStatus })
  originalStatus!: TransactionStatus;

  @Column({ type: "enum", enum: TransactionStatus })
  newStatus!: TransactionStatus;

  @Column()
  performedByUserId!: number;

  @Column()
  reason!: string;

  @CreateDateColumn({ type: "timestamptz" })
  createdAt!: Date;

  @ManyToOne(() => MilkReceptionTransaction)
  @JoinColumn({ name: "transactionId" })
  transaction!: MilkReceptionTransaction;

  @ManyToOne(() => User)
  @JoinColumn({ name: "performedByUserId" })
  performedBy!: User;
}
