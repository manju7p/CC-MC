import { Column, Entity, PrimaryGeneratedColumn } from "typeorm";

@Entity("permissions")
export class Permission {
  @PrimaryGeneratedColumn()
  id!: number;

  @Column({ unique: true })
  code!: string;

  @Column()
  module!: string;

  @Column()
  action!: string;
}
