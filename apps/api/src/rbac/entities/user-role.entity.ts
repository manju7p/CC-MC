import { Entity, JoinColumn, ManyToOne, PrimaryColumn } from "typeorm";
import { User } from "../../auth/entities/user.entity";
import { Role } from "./role.entity";

/** Explicit join entity (User <-> Role). */
@Entity("user_roles")
export class UserRole {
  @PrimaryColumn()
  userId!: number;

  @PrimaryColumn()
  roleId!: number;

  @ManyToOne(() => User, { onDelete: "CASCADE" })
  @JoinColumn({ name: "userId" })
  user!: User;

  @ManyToOne(() => Role, { onDelete: "CASCADE" })
  @JoinColumn({ name: "roleId" })
  role!: Role;
}
