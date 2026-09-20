USE [Crypto];
GO

IF OBJECT_ID(N'[dbo].[AuditEvents]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[AuditEvents]
    (
        [Id] UNIQUEIDENTIFIER NOT NULL,
        [ActorUserId] UNIQUEIDENTIFIER NULL,
        [Action] NVARCHAR(128) NOT NULL,
        [TargetType] NVARCHAR(128) NOT NULL,
        [TargetId] NVARCHAR(256) NOT NULL,
        [OccurredAtUtc] DATETIMEOFFSET NOT NULL,
        [Before] NVARCHAR(MAX) NULL,
        [After] NVARCHAR(MAX) NULL,
        [CorrelationId] NVARCHAR(128) NOT NULL,
        CONSTRAINT [PK_AuditEvents] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END;
GO

IF OBJECT_ID(N'[dbo].[Invitations]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Invitations]
    (
        [Id] UNIQUEIDENTIFIER NOT NULL,
        [Code] NVARCHAR(64) NOT NULL,
        [IssuedByUserId] UNIQUEIDENTIFIER NOT NULL,
        [MaxUses] INT NOT NULL,
        [UsedCount] INT NOT NULL,
        [ExpiresAtUtc] DATETIMEOFFSET NOT NULL,
        [IsActive] BIT NOT NULL,
        CONSTRAINT [PK_Invitations] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END;
GO

IF OBJECT_ID(N'[dbo].[Users]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Users]
    (
        [Id] UNIQUEIDENTIFIER NOT NULL,
        [Email] NVARCHAR(254) NOT NULL,
        [DisplayName] NVARCHAR(200) NOT NULL,
        [Locale] NVARCHAR(16) NOT NULL,
        [TimeZone] NVARCHAR(64) NOT NULL,
        [ReportingCurrency] NVARCHAR(3) NOT NULL,
        [Role] INT NOT NULL,
        [MultiFactorAuthenticationEnabled] BIT NOT NULL,
        [Status] INT NOT NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AuditEvents_CorrelationId' AND object_id = OBJECT_ID(N'[dbo].[AuditEvents]'))
BEGIN
    CREATE INDEX [IX_AuditEvents_CorrelationId] ON [dbo].[AuditEvents] ([CorrelationId] ASC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Invitations_Code' AND object_id = OBJECT_ID(N'[dbo].[Invitations]'))
BEGIN
    CREATE UNIQUE INDEX [IX_Invitations_Code] ON [dbo].[Invitations] ([Code] ASC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Users_Email' AND object_id = OBJECT_ID(N'[dbo].[Users]'))
BEGIN
    CREATE UNIQUE INDEX [IX_Users_Email] ON [dbo].[Users] ([Email] ASC);
END;
GO
