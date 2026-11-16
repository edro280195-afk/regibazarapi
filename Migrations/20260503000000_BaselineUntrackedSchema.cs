using EntregasApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <summary>
    /// Esquema que entró al modelo el 2026-05-03 sin una migración que lo creara (existe en
    /// producción porque se aplicó por fuera), pero que la cadena de migraciones da por hecho:
    ///   · Módulo Tanda: tablas <c>products</c>, <c>tandas</c>, <c>tanda_participants</c> y <c>payments</c>.
    ///     La migración de Sorteos (20260503233231_AddRaffleModuleEnhanced) ya las referencia por
    ///     llave foránea; por eso una base nueva fallaba con «relation "products" does not exist».
    ///   · Columnas <c>Orders.ScheduledDeliveryDate</c> y <c>Users.Rol</c>: están en el snapshot desde
    ///     esa misma fecha, pero ninguna migración las agrega, así que una base nueva quedaba sin ellas.
    ///   · <c>ChatMessages.DeliveryRouteId</c> debe aceptar nulos (el chat de una clienta cuyo pedido aún
    ///     no está en ruta lo inserta nulo), pero la cadena lo deja NOT NULL.
    ///
    /// Se crean con el esquema que tenían el 2026-05-03 (snapshot de la migración de Sorteos); las
    /// migraciones posteriores siguen agregando sus columnas como siempre.
    ///
    /// En producción NO hace nada: cada pieza se crea solo si no existe, y ahí ya existen.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260503000000_BaselineUntrackedSchema")]
    public class BaselineUntrackedSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $baseline$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 'public' AND table_name = 'Orders' AND column_name = 'ScheduledDeliveryDate') THEN
        ALTER TABLE ""Orders"" ADD COLUMN ""ScheduledDeliveryDate"" timestamp with time zone;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 'public' AND table_name = 'Users' AND column_name = 'Rol') THEN
        -- Los usuarios que ya existan quedan como Admin (el valor por defecto del modelo).
        ALTER TABLE ""Users"" ADD COLUMN ""Rol"" text NOT NULL DEFAULT 'Admin';
        ALTER TABLE ""Users"" ALTER COLUMN ""Rol"" DROP DEFAULT;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_schema = 'public' AND table_name = 'ChatMessages'
                 AND column_name = 'DeliveryRouteId' AND is_nullable = 'NO') THEN
        ALTER TABLE ""ChatMessages"" ALTER COLUMN ""DeliveryRouteId"" DROP NOT NULL;
    END IF;

    IF to_regclass('public.products') IS NULL THEN
        CREATE TABLE products (
            id uuid NOT NULL,
            base_price numeric(12,2) NOT NULL,
            created_at timestamp with time zone NOT NULL,
            description text,
            is_active boolean NOT NULL,
            name character varying(255) NOT NULL,
            CONSTRAINT ""PK_products"" PRIMARY KEY (id)
        );
    END IF;

    IF to_regclass('public.tandas') IS NULL THEN
        CREATE TABLE tandas (
            id uuid NOT NULL,
            access_token character varying(64) NOT NULL,
            created_at timestamp with time zone NOT NULL,
            name character varying(255) NOT NULL,
            penalty_amount numeric(12,2) NOT NULL,
            product_id uuid NOT NULL,
            start_date date NOT NULL,
            status character varying(50) NOT NULL,
            total_weeks integer NOT NULL,
            weekly_amount numeric(12,2) NOT NULL,
            CONSTRAINT ""PK_tandas"" PRIMARY KEY (id),
            CONSTRAINT ""FK_tandas_products_product_id"" FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE CASCADE
        );
        CREATE INDEX ""IX_tandas_product_id"" ON tandas (product_id);
    END IF;

    IF to_regclass('public.tanda_participants') IS NULL THEN
        CREATE TABLE tanda_participants (
            id uuid NOT NULL,
            assigned_turn integer NOT NULL,
            customer_id integer NOT NULL,
            delivery_date date,
            is_delivered boolean NOT NULL,
            status character varying(50) NOT NULL,
            tanda_id uuid NOT NULL,
            variant character varying(255),
            CONSTRAINT ""PK_tanda_participants"" PRIMARY KEY (id),
            CONSTRAINT ""FK_tanda_participants_Clients_customer_id"" FOREIGN KEY (customer_id) REFERENCES ""Clients"" (""Id"") ON DELETE CASCADE,
            CONSTRAINT ""FK_tanda_participants_tandas_tanda_id"" FOREIGN KEY (tanda_id) REFERENCES tandas (id) ON DELETE CASCADE
        );
        CREATE INDEX ""IX_tanda_participants_customer_id"" ON tanda_participants (customer_id);
        CREATE UNIQUE INDEX ""IX_TandaParticipant_Tanda_Turn"" ON tanda_participants (tanda_id, assigned_turn);
    END IF;

    IF to_regclass('public.payments') IS NULL THEN
        CREATE TABLE payments (
            id uuid NOT NULL,
            amount_paid numeric(12,2) NOT NULL,
            is_verified boolean NOT NULL,
            notes text,
            participant_id uuid NOT NULL,
            payment_date timestamp with time zone NOT NULL,
            penalty_paid numeric(12,2) NOT NULL,
            week_number integer NOT NULL,
            CONSTRAINT ""PK_payments"" PRIMARY KEY (id),
            CONSTRAINT ""FK_payments_tanda_participants_participant_id"" FOREIGN KEY (participant_id) REFERENCES tanda_participants (id) ON DELETE CASCADE
        );
        CREATE INDEX ""IX_payments_participant_id"" ON payments (participant_id);
    END IF;
END
$baseline$;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intencionalmente vacío: en producción estas tablas existían antes de esta migración
            // y contienen datos reales; revertirla jamás debe borrarlas.
        }
    }
}
