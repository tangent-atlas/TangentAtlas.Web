@{
    # -------------------------------------------------------------------------
    # SHARED / corporate-level symbols ONLY.
    #
    # A value belongs here only if MORE THAN ONE resource references it. Anything
    # specific to a single resource lives in that resource's deploy script, not
    # here - which keeps this file small as the number of resources grows.
    # -------------------------------------------------------------------------

    # Subscription every resource is built into.
    SubscriptionId = 'bcbc34b4-bba4-4956-bf97-ffbca3dcb1a5'

    # Resource groups, keyed by a short logical name the deploy scripts use.
    ResourceGroups = @{
        Marketing   = @{ Name = 'Marketing';   Location = 'eastus' }
        Development = @{ Name = 'Development'; Location = 'canadacentral' }
    }

    # Shared secret repository.
    KeyVaultName = 'tangent-atlas-vault'

    # Organization admin principal (SQL admin today; also expected in Key Vault
    # access policies). Shared because multiple resources grant to it.
    EntraAdmin = @{
        Name     = 'donald.airey@tangentatlas.com'
        ObjectId = '03560461-7a02-45a6-97f9-b9508604cad6'
    }
}
