-- Application users with fine-grained permissions, product license, installation settings.

CREATE TABLE dsi.AppUser
(
    UserId             INT            IDENTITY(1,1) NOT NULL CONSTRAINT PK_AppUser PRIMARY KEY,
    UserName           NVARCHAR(64)   NOT NULL CONSTRAINT UQ_AppUser_UserName UNIQUE,
    FullName           NVARCHAR(150)  NOT NULL,
    Email              NVARCHAR(200)  NULL,
    PasswordHash       NVARCHAR(400)  NOT NULL,          -- PBKDF2-SHA256, salted, never the password itself
    IsActive           BIT            NOT NULL CONSTRAINT DF_AppUser_IsActive DEFAULT (1),
    MustChangePassword BIT            NOT NULL CONSTRAINT DF_AppUser_MustChange DEFAULT (0),
    SecurityStamp      UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_AppUser_Stamp DEFAULT (NEWID()),   -- changes => sessions signed out
    FailedLogins       INT            NOT NULL CONSTRAINT DF_AppUser_Failed DEFAULT (0),
    LockedUntil        DATETIME2(3)   NULL,
    LastLoginAt        DATETIME2(3)   NULL,
    CreatedAt          DATETIME2(3)   NOT NULL CONSTRAINT DF_AppUser_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt          DATETIME2(3)   NOT NULL CONSTRAINT DF_AppUser_UpdatedAt DEFAULT (SYSUTCDATETIME())
);
GO
CREATE TABLE dsi.AppUserPermission
(
    UserId     INT          NOT NULL CONSTRAINT FK_AppUserPermission_User REFERENCES dsi.AppUser (UserId) ON DELETE CASCADE,
    Permission NVARCHAR(32) NOT NULL,   -- Admin, Scan, Review, Reports, MasterData, ReportDesign, Users, License
    CONSTRAINT PK_AppUserPermission PRIMARY KEY (UserId, Permission)
);
GO
-- Activated license keys (latest active row is the current license). The key is signed by Helao2; the product
-- only holds the public key, so keys cannot be forged or edited.
CREATE TABLE dsi.License
(
    LicenseRowId BIGINT         IDENTITY(1,1) NOT NULL CONSTRAINT PK_License PRIMARY KEY,
    LicenseId    UNIQUEIDENTIFIER NOT NULL,
    LicenseKey   NVARCHAR(4000) NOT NULL,
    LicensedTo   NVARCHAR(200)  NOT NULL,
    LicenseType  NVARCHAR(20)   NOT NULL,
    ExpiresAt    DATETIME2(3)   NULL,
    ActivatedAt  DATETIME2(3)   NOT NULL CONSTRAINT DF_License_ActivatedAt DEFAULT (SYSUTCDATETIME()),
    ActivatedBy  NVARCHAR(64)   NOT NULL,
    IsCurrent    BIT            NOT NULL CONSTRAINT DF_License_IsCurrent DEFAULT (1)
);
GO
CREATE TABLE dsi.AppSetting
(
    SettingKey   NVARCHAR(64)   NOT NULL CONSTRAINT PK_AppSetting PRIMARY KEY,
    SettingValue NVARCHAR(4000) NULL,
    UpdatedAt    DATETIME2(3)   NOT NULL CONSTRAINT DF_AppSetting_UpdatedAt DEFAULT (SYSUTCDATETIME())
);
GO
-- Installation id: printed on the activation page; Helao2 can bind a license to it.
INSERT dsi.AppSetting (SettingKey, SettingValue) VALUES (N'InstallationId', UPPER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')));
GO
ALTER TABLE dsi.ShiftMaster ADD IsActive BIT NOT NULL CONSTRAINT DF_ShiftMaster_IsActive DEFAULT (1);
GO
