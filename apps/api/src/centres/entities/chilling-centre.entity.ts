import { Column, CreateDateColumn, Entity, PrimaryGeneratedColumn, UpdateDateColumn } from "typeorm";
import { RecordStatus } from "@cc-mc/shared-types";

@Entity("chilling_centres")
export class ChillingCentre {
  @PrimaryGeneratedColumn()
  id!: number;

  @Column({ unique: true })
  code!: string;

  @Column()
  name!: string;

  @Column({ type: "enum", enum: RecordStatus, default: RecordStatus.ACTIVE })
  status!: RecordStatus;

  @CreateDateColumn({ type: "timestamptz" })
  createdAt!: Date;

  @UpdateDateColumn({ type: "timestamptz" })
  updatedAt!: Date;
}
