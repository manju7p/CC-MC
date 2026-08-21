import { Column, CreateDateColumn, Entity, PrimaryGeneratedColumn, UpdateDateColumn } from "typeorm";
import { RecordStatus } from "@cc-mc/shared-types";

@Entity("users")
export class User {
  @PrimaryGeneratedColumn()
  id!: number;

  @Column({ unique: true })
  email!: string;

  @Column()
  passwordHash!: string;

  @Column()
  fullName!: string;

  @Column({ type: "enum", enum: RecordStatus, default: RecordStatus.ACTIVE })
  status!: RecordStatus;

  @CreateDateColumn({ type: "timestamptz" })
  createdAt!: Date;

  @UpdateDateColumn({ type: "timestamptz" })
  updatedAt!: Date;
}
