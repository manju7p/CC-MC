import { Column, CreateDateColumn, Entity, Index, PrimaryGeneratedColumn } from "typeorm";

/**
 * oldValue/newValue are jsonb deliberately - the audited resources are
 * heterogeneous (users, sources, vehicles, quality rules, transactions),
 * and a generic diff column avoids one audit table per resource type. This
 * is the one place in the schema where JSON is used, per the "avoid
 * unnecessary JSON fields" guideline - it's the genuinely variable case.
 */
@Entity("audit_logs")
@Index(["resourceType", "resourceId"])
@Index(["userId", "createdAt"])
export class AuditLog {
  @PrimaryGeneratedColumn()
  id!: number;

  @Column({ nullable: true, type: "int" })
  userId!: number | null;

  @Column({ nullable: true, type: "int" })
  centreId!: number | null;

  @Column()
  action!: string;

  @Column()
  resourceType!: string;

  @Column()
  resourceId!: string;

  @Column({ type: "jsonb", nullable: true })
  oldValue!: unknown;

  @Column({ type: "jsonb", nullable: true })
  newValue!: unknown;

  @Column({ type: "varchar", nullable: true })
  reason!: string | null;

  @CreateDateColumn({ type: "timestamptz" })
  createdAt!: Date;
}
