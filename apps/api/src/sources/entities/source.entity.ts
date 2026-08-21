import { Column, CreateDateColumn, Entity, JoinColumn, ManyToOne, PrimaryGeneratedColumn, UpdateDateColumn } from "typeorm";
import { RecordStatus } from "@cc-mc/shared-types";
import { ChillingCentre } from "../../centres/entities/chilling-centre.entity";

@Entity("sources")
export class Source {
  @PrimaryGeneratedColumn()
  id!: number;

  @Column({ unique: true })
  code!: string;

  @Column()
  name!: string;

  @Column({ type: "varchar", nullable: true })
  location!: string | null;

  @Column({ type: "varchar", nullable: true })
  contact!: string | null;

  @Column({ type: "varchar", nullable: true })
  milkType!: string | null;

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
