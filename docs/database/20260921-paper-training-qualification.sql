SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.PaperTrainingActivations', 'SlotsJson') IS NULL
BEGIN
    ALTER TABLE dbo.PaperTrainingActivations
        ADD SlotsJson nvarchar(max) NOT NULL
            CONSTRAINT DF_PaperTrainingActivations_SlotsJson DEFAULT (N'[]') WITH VALUES;
END;

IF COL_LENGTH('dbo.PaperTrainingActivations', 'QualificationsJson') IS NULL
BEGIN
    ALTER TABLE dbo.PaperTrainingActivations
        ADD QualificationsJson nvarchar(max) NOT NULL
            CONSTRAINT DF_PaperTrainingActivations_QualificationsJson DEFAULT (N'[]') WITH VALUES;
END;

COMMIT TRANSACTION;
