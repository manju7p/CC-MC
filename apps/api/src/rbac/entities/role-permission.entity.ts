import { Entity, JoinColumn, ManyToOne, PrimaryColumn } from "typeorm";
import { Role } from "./role.entity";
import { Permission } from "./permission.entity";

/** Explicit join entity (Role <-> Permission) - explicit FKs per DB guidelines, no implicit @ManyToMany magic table. */
@Entity("role_permissions")
export class RolePermission {
  @PrimaryColumn()
  roleId!: number;

  @PrimaryColumn()
  permissionId!: number;

  @ManyToOne(() => Role, { onDelete: "CASCADE" })
  @JoinColumn({ name: "roleId" })
  role!: Role;

  @ManyToOne(() => Permission, { onDelete: "CASCADE" })
  @JoinColumn({ name: "permissionId" })
  permission!: Permission;
}
