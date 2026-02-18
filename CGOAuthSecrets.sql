/*
  CGOAuthSecrets.sql
  Creates the SQL objects required by SqlBlackbaudSecretStore.cs:
    - dbo.CGOAuthSecrets (table)
    - dbo.CGOAuthSecrets_Upsert (stored procedure)
    - dbo.CGOAuthSecrets_Get (stored procedure)

  Notes:
    - Tokens + subscription key are stored ENCRYPTED using ENCRYPTBYPASSPHRASE.
    - Passing NULL for a field means "leave existing value unchanged".
    - The GLOBAL row key is "__GLOBAL__" (stores subscription key once for all machines).
    - Machine rows are keyed like: "MACHINE:COMPUTERNAME"
*/

IF OBJECT_ID('dbo.CGOAuthSecrets', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CGOAuthSecrets
    (
        SecretKey            NVARCHAR(200) NOT NULL CONSTRAINT PK_CGOAuthSecrets PRIMARY KEY,
        AccessTokenEnc       VARBINARY(MAX) NULL,
        RefreshTokenEnc      VARBINARY(MAX) NULL,
        ExpiresAtUtc         DATETIME2(0) NULL,
        TokenType            NVARCHAR(50) NULL,
        Scope                NVARCHAR(4000) NULL,
        SubscriptionKeyEnc   VARBINARY(MAX) NULL,
        CreatedAtUtc         DATETIME2(0) NOT NULL CONSTRAINT DF_CGOAuthSecrets_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc         DATETIME2(0) NOT NULL CONSTRAINT DF_CGOAuthSecrets_UpdatedAtUtc DEFAULT (SYSUTCDATETIME())
    );
END
GO

-- Helpful index if you ever query by expiry in the future.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CGOAuthSecrets_ExpiresAtUtc' AND object_id = OBJECT_ID('dbo.CGOAuthSecrets'))
BEGIN
    CREATE INDEX IX_CGOAuthSecrets_ExpiresAtUtc ON dbo.CGOAuthSecrets(ExpiresAtUtc);
END
GO

CREATE OR ALTER PROCEDURE dbo.CGOAuthSecrets_Upsert
    @SecretKey        NVARCHAR(200),
    @Passphrase       NVARCHAR(200),
    @AccessToken      NVARCHAR(MAX) = NULL,
    @RefreshToken     NVARCHAR(MAX) = NULL,
    @ExpiresAtUtc     DATETIME2(0)  = NULL,
    @TokenType        NVARCHAR(50)  = NULL,
    @Scope            NVARCHAR(4000)= NULL,
    @SubscriptionKey  NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @SecretKey IS NULL OR LTRIM(RTRIM(@SecretKey)) = ''
        THROW 50000, 'SecretKey is required.', 1;

    IF @Passphrase IS NULL OR LTRIM(RTRIM(@Passphrase)) = ''
        THROW 50000, 'Passphrase is required.', 1;

    IF EXISTS (SELECT 1 FROM dbo.CGOAuthSecrets WHERE SecretKey = @SecretKey)
    BEGIN
        UPDATE dbo.CGOAuthSecrets
        SET
            AccessTokenEnc =
                CASE WHEN @AccessToken IS NULL THEN AccessTokenEnc
                     ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @AccessToken) END,

            RefreshTokenEnc =
                CASE WHEN @RefreshToken IS NULL THEN RefreshTokenEnc
                     ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @RefreshToken) END,

            ExpiresAtUtc = COALESCE(@ExpiresAtUtc, ExpiresAtUtc),

            TokenType = COALESCE(@TokenType, TokenType),
            Scope     = COALESCE(@Scope, Scope),

            SubscriptionKeyEnc =
                CASE WHEN @SubscriptionKey IS NULL THEN SubscriptionKeyEnc
                     ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @SubscriptionKey) END,

            UpdatedAtUtc = SYSUTCDATETIME()
        WHERE SecretKey = @SecretKey;
    END
    ELSE
    BEGIN
        INSERT dbo.CGOAuthSecrets
        (
            SecretKey,
            AccessTokenEnc,
            RefreshTokenEnc,
            ExpiresAtUtc,
            TokenType,
            Scope,
            SubscriptionKeyEnc
        )
        VALUES
        (
            @SecretKey,
            CASE WHEN @AccessToken IS NULL THEN NULL ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @AccessToken) END,
            CASE WHEN @RefreshToken IS NULL THEN NULL ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @RefreshToken) END,
            @ExpiresAtUtc,
            @TokenType,
            @Scope,
            CASE WHEN @SubscriptionKey IS NULL THEN NULL ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @SubscriptionKey) END
        );
    END
END
GO

CREATE OR ALTER PROCEDURE dbo.CGOAuthSecrets_Get
    @SecretKey   NVARCHAR(200),
    @Passphrase  NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        AccessToken =
            CONVERT(NVARCHAR(MAX), DECRYPTBYPASSPHRASE(@Passphrase, AccessTokenEnc)),
        RefreshToken =
            CONVERT(NVARCHAR(MAX), DECRYPTBYPASSPHRASE(@Passphrase, RefreshTokenEnc)),
        ExpiresAtUtc = CONVERT(DATETIME2(0), ExpiresAtUtc),
        TokenType = TokenType,
        Scope = Scope,
        SubscriptionKey =
            CONVERT(NVARCHAR(MAX), DECRYPTBYPASSPHRASE(@Passphrase, SubscriptionKeyEnc))
    FROM dbo.CGOAuthSecrets
    WHERE SecretKey = @SecretKey;
END
GO
