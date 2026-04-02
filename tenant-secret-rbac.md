# Tenant Secret RBAC

Use [tenant-secret-rbac.yaml](/E:/Duende.IdentityServer.Admin/tenant-secret-rbac.yaml) when `TenantSecretSource:UseKubernetesSecrets=true`.

This grants the STS and Admin API pods permission to read Kubernetes `Secret` objects in the `identity-platform` namespace.

Required deployment wiring:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ids-admin-api
spec:
  template:
    spec:
      serviceAccountName: ids-admin-api
```

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ids-sts
spec:
  template:
    spec:
      serviceAccountName: ids-sts
```

If your tenant secrets live in another namespace, change:

- `metadata.namespace`
- each `subjects[].namespace`
- `TenantSecretSource:KubernetesNamespace` in app config

If you store tenant secrets across multiple namespaces, create one `Role` + `RoleBinding` set per namespace, or switch to a narrowly-scoped `ClusterRole` only if you truly need cross-namespace reads.
