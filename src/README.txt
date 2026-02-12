A la racine faire:

docker compose build
docker compose up -d

IMPORTANT : lors de la première utilisation, bien attendre que l'initialisation (téléchargement des livres + construction du graphe)soit terminé (il faut compter 10 minutes). On peut suivre l'évolution de l'initialisation en faisant: 

docker logs -f library_api


L'application sera disponible à l'url suivante:

http://localhost/

