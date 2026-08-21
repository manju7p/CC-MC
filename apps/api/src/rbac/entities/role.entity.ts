import { Column, Entity, OneToMany, PrimaryGeneratedColumn } from "typeorm";
import type { RolePermission } from "./role-permission.entity";

@Entity("roles")
export class Role {
  @PrimaryGeneratedColumn()
  id!: number;

  @Column({ unique: true })
  name!: string;

  @Column({ default: true })
  isSystemDefault!: boolean;

  @OneToMany("RolePermission", (rp: RolePermission) => rp.role)
  rolePermissions!: RolePermission[];
}
