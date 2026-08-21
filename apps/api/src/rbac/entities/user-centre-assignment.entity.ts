import { Column, Entity, JoinColumn, ManyToOne, PrimaryGeneratedColumn, Unique } from "typeorm";
import { User } from "../../auth/entities/user.entity";
import { ChillingCentre } from "../../centres/entities/chilling-centre.entity";

/**
 * See docs/assumptions.md #centre-assignment-model. `allCentres = true`
 * with `centreId = null` represents organization-wide access; otherwise
 * one row per centre the user is assigned to. Postgres treats multiple
 * NULL centreId rows as distinct under the unique constraint, so
 * "only one ALL row per user" is enforced in application code
 * (AuthService/seed), not by the schema itself.
 */
@Entity("user_centre_assignments")
@Unique(["userId", "centreId"])
export class UserCentreAssignment {
  @PrimaryGeneratedColumn()
  id!: number;

  @Column()
  userId!: number;

  @Column({ nullable: true, type: "int" })
  centreId!: number | null;

  @Column({ default: false })
  allCentres!: boolean;

  @ManyToOne(() => User, { onDelete: "CASCADE" })
  @JoinColumn({ name: "userId" })
  user!: User;

  @ManyToOne(() => ChillingCentre, { onDelete: "CASCADE", nullable: true })
  @JoinColumn({ name: "centreId" })
  centre!: ChillingCentre | null;
}
