import { Column, Entity, JoinColumn, ManyToOne, PrimaryGeneratedColumn, Unique } from "typeorm";
import { QualityParameter } from "@cc-mc/shared-types";
import { ChillingCentre } from "../../centres/entities/chilling-centre.entity";

/** centreId = null means a global default rule; a centre-specific row overrides it for that centre. */
@Entity("quality_rules")
@Unique(["parameter", "centreId"])
export class QualityRule {
  @PrimaryGeneratedColumn()
  id!: number;

  @Column({ type: "enum", enum: QualityParameter })
  parameter!: QualityParameter;

  @Column("decimal", { precision: 6, scale: 2 })
  minValue!: string;

  @Column("decimal", { precision: 6, scale: 2 })
  maxValue!: string;

  @Column({ nullable: true, type: "int" })
  centreId!: number | null;

  @ManyToOne(() => ChillingCentre, { nullable: true })
  @JoinColumn({ name: "centreId" })
  centre!: ChillingCentre | null;
}
