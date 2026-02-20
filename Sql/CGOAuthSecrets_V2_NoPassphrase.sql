USE [CheerfulGiver];
GO

/* ---------------------------------------------------------------------------
   V2 procs: NO PASSPHRASE
   These procs simply read/write the encrypted VARBINARY columns in dbo.CGOAuthSecrets.
   Encryption/decryption happens in the app (DPAPI) via SqlBlackbaudSecretStore DPAPI mode.
--------------------------------------------------------------------------- */

CREATE OR ALTER PROCEDURE [dbo].[CGOAuthSecrets_Get2]
    @SecretKey NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        AccessTokenEnc,
        RefreshTokenEnc,
        ExpiresAtUtc = CONVERT(DATETIME2(0), ExpiresAtUtc),
        TokenType,
        Scope,
        SubscriptionKeyEnc
    FROM dbo.CGOAuthSecrets
    WHERE SecretKey = @SecretKey;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[CGOAuthSecrets_Upsert2]
    @SecretKey          NVARCHAR(128),
    @AccessTokenEnc     VARBINARY(MAX) = NULL,
    @RefreshTokenEnc    VARBINARY(MAX) = NULL,
    @ExpiresAtUtc       DATETIME2(0)   = NULL,
    @TokenType          NVARCHAR(50)   = NULL,
    @Scope              NVARCHAR(4000) = NULL,
    @SubscriptionKeyEnc VARBINARY(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @SecretKey IS NULL OR LTRIM(RTRIM(@SecretKey)) = ''
        THROW 50000, 'SecretKey is required.', 1;

    IF EXISTS (SELECT 1 FROM dbo.CGOAuthSecrets WHERE SecretKey = @SecretKey)
    BEGIN
        UPDATE dbo.CGOAuthSecrets
        SET
            AccessTokenEnc     = COALESCE(@AccessTokenEnc, AccessTokenEnc),
            RefreshTokenEnc    = COALESCE(@RefreshTokenEnc, RefreshTokenEnc),
            ExpiresAtUtc       = COALESCE(@ExpiresAtUtc, ExpiresAtUtc),
            TokenType          = COALESCE(@TokenType, TokenType),
            Scope              = COALESCE(@Scope, Scope),
            SubscriptionKeyEnc = COALESCE(@SubscriptionKeyEnc, SubscriptionKeyEnc),
            UpdatedAtUtc       = SYSUTCDATETIME(),
            ModifiedAtUtc      = SYSUTCDATETIME()
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
            SubscriptionKeyEnc,
            ModifiedAtUtc
        )
        VALUES
        (
            @SecretKey,
            @AccessTokenEnc,
            @RefreshTokenEnc,
            @ExpiresAtUtc,
            @TokenType,
            @Scope,
            @SubscriptionKeyEnc,
            SYSUTCDATETIME()
        );
    END
END
GO
