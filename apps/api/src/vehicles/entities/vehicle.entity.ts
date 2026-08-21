import { Column, CreateDateColumn, Entity, JoinColumn, ManyToOne, PrimaryGeneratedColumn, UpdateDateColumn } from "typeorm";
import { RecordStatus } from "@cc-mc/shared-types";
import { ChillingCentre } from "../../centres/entities/chilling-centre.entity";

@Entity("vehicles")
export class Vehicle {
  @PrimaryGeneratedColumn()
  id!: number;

  @Column({ unique: true })
  vehicleNumber!: string;

  @Column({ type: "varchar", nullable: true })
  tankerNumber!: string | null;

  @Column({ type: "varchar", nullable: true })
  driverName!: string | null;

  @Column({ type: "varchar", nullable: true })
  driverMobile!: string | null;

  @Column("decimal", { precision: 10, scale: 2, nullable: true })
  capacityKg!: string | null;

  @Column({ type: "enum", enum: RecordStatus, default: RecordStatus.ACTIVE })
  status!: RecordStatus;

  @Column()
  centreId!: number;

  @ManyToOne(() => ChillingCentre)
  @JoinColumn({ name: "centreId" })
  centre!: ChillingCentre;

  @CreateDateColumn({ type: "timestamptz" })
  createdAt!: Date;

  @UpdateDateColumn({ type: "timestamptz" })
  updatedAt!: Date;
}
