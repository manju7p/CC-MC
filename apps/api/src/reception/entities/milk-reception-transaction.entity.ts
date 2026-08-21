import {
  Column,
  CreateDateColumn,
  Entity,
  Index,
  JoinColumn,
  ManyToOne,
  PrimaryGeneratedColumn,
  UpdateDateColumn,
} from "typeorm";
import { ReadingSource, TransactionStatus } from "@cc-mc/shared-types";
import { ChillingCentre } from "../../centres/entities/chilling-centre.entity";
import { Source } from "../../sources/entities/source.entity";
import { Vehicle } from "../../vehicles/entities/vehicle.entity";
import { User } from "../../auth/entities/user.entity";

@Entity("milk_reception_transactions")
@Index(["centreId", "receivedAt"])
@Index(["centreId", "status"])
export class MilkReceptionTransaction {
  @PrimaryGeneratedColumn()
  id!: number;

  @Column({ unique: true })
  transactionNumber!: string;

  @Column()
  centreId!: number;

  @Column()
  sourceId!: number;

  @Column()
  vehicleId!: number;

  @Column()
  operatorUserId!: number;

  @Column("decimal", { precision: 10, scale: 2 })
  quantityKg!: string;

  @Column("decimal", { precision: 5, scale: 2 })
  fat!: string;

  @Column("decimal", { precision: 5, scale: 2 })
  snf!: string;

  @Column("decimal", { precision: 5, scale: 2 })
  temperature!: string;

  @Column({ type: "enum", enum: TransactionStatus })
  status!: TransactionStatus;

  @Column({ type: "enum", enum: ReadingSource, default: ReadingSource.MANUAL })
  readingSource!: ReadingSource;

  @Column({ type: "varchar", nullable: true })
  reason!: string | null;

  // Reserved for the future Local Device Gateway sync design (Rule 3).
  // Always null in the MVP slice.
  @Column({ type: "varchar", nullable: true, unique: true })
  localIdempotencyKey!: string | null;

  @Column({ type: "timestamptz", default: () => "now()" })
  receivedAt!: Date;

  @CreateDateColumn({ type: "timestamptz" })
  createdAt!: Date;

  @UpdateDateColumn({ type: "timestamptz" })
  updatedAt!: Date;

  @ManyToOne(() => ChillingCentre)
  @JoinColumn({ name: "centreId" })
  centre!: ChillingCentre;

  @ManyToOne(() => Source)
  @JoinColumn({ name: "sourceId" })
  source!: Source;

  @ManyToOne(() => Vehicle)
  @JoinColumn({ name: "vehicleId" })
  vehicle!: Vehicle;

  @ManyToOne(() => User)
  @JoinColumn({ name: "operatorUserId" })
  operator!: User;
}
