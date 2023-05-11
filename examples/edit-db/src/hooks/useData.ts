import { useQuery } from "@apollo/client";
import { GET_SCHEMA } from "../common/schema";

export function useSchema() {
    const { loading, error, data } = useQuery(GET_SCHEMA);

    if (!data)
        return { loading, error, data };

    const columns = (data?.schema?.queryType?.fields ?? [])
        .map((f: any) => ({
            ...f,
            columns: f?.type?.fields
                ?.find((d: any) => d.name === "data")
                ?.type?.ofType.fields
                ?.filter((c: any) => c.name.startsWith('_') == false)
                ?.map((c: any) => ({ ...c, paramType: c?.type?.ofType?.name ?? "String" }))
        }
        ))
        ?.sort((a: any, b: any) => {
            if (a.name === "id") return -1;
            if (b.name === "id") return 1;
            return a.name.localeCompare(b.name);})
    return { loading, error, data: columns };
}